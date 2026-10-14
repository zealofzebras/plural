import copy
import tempfile
import tomllib
import unittest
from pathlib import Path
from urllib.parse import parse_qs
import zipfile
from unittest.mock import Mock, patch

from google_play import PACKAGE_NAME, publish as publish_google_play
from microsoft_store import IMAGE_TYPES, StoreClient, prepare_submission, publish, safe_message, wait_for_commit, write_archive
from store_assets import LIMITS, load_texts, Report, render_logo, validate, windows_listing


class GooglePlayTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.assets = Path(self.directory.name)
        self.locale = self.assets / "metadata" / "android" / "en-US"
        (self.locale / "changelogs").mkdir(parents=True)
        for filename, text in (("title.txt", "Plural"), ("short_description.txt", "Record audio"),
                               ("full_description.txt", "Record independent tracks"), ("changelogs/1020300.txt", "Fixes")):
            (self.locale / filename).write_text(text, encoding="utf-8")
        (self.locale / "images" / "phoneScreenshots").mkdir(parents=True)
        (self.locale / "images" / "icon.png").write_bytes(b"icon")
        (self.locale / "images" / "phoneScreenshots" / "01.png").write_bytes(b"screenshot")
        self.package = self.assets / "app.aab"
        self.package.write_bytes(b"bundle")
        self.service = Mock()
        self.edits = self.service.edits.return_value
        self.edits.insert.return_value.execute.return_value = {"id": "edit-1"}
        self.upload = self.edits.bundles.return_value.upload.return_value
        self.upload.next_chunk.side_effect = [(None, None), (None, {"versionCode": 1020300})]

    def submit(self):
        return publish_google_play(self.service, self.assets, self.package, 1020300, "1.2.3",
                                   "internal", "completed", Mock())

    def test_uploads_bundle_listing_images_and_commits_internal_release(self):
        self.assertEqual(self.submit(), "edit-1")
        self.assertEqual(self.upload.next_chunk.call_count, 2)
        self.edits.listings.return_value.update.assert_called_once_with(
            packageName=PACKAGE_NAME, editId="edit-1", language="en-US",
            body={"title": "Plural", "shortDescription": "Record audio", "fullDescription": "Record independent tracks"})
        self.assertEqual(self.edits.images.return_value.upload.call_count, 2)
        self.assertEqual(self.edits.images.return_value.deleteall.call_count, 2)
        self.edits.tracks.return_value.update.assert_called_once_with(
            packageName=PACKAGE_NAME, editId="edit-1", track="internal",
            body={"track": "internal", "releases": [{"name": "1.2.3", "versionCodes": ["1020300"],
                  "status": "completed", "releaseNotes": [{"language": "en-US", "text": "Fixes"}]}]})
        self.edits.validate.assert_called_once_with(packageName=PACKAGE_NAME, editId="edit-1")
        self.edits.commit.assert_called_once_with(packageName=PACKAGE_NAME, editId="edit-1")
        self.edits.delete.assert_not_called()

    def test_upload_failure_abandons_edit_without_committing(self):
        self.upload.next_chunk.side_effect = RuntimeError("upload failed")
        with self.assertRaisesRegex(RuntimeError, "upload failed"):
            self.submit()
        self.edits.delete.assert_called_once_with(packageName=PACKAGE_NAME, editId="edit-1")
        self.edits.commit.assert_not_called()

    def test_wrong_bundle_version_abandons_edit(self):
        self.upload.next_chunk.side_effect = [(None, {"versionCode": 1})]
        with self.assertRaisesRegex(ValueError, "versionCode"):
            self.submit()
        self.edits.delete.assert_called_once()
        self.edits.tracks.return_value.update.assert_not_called()

    def test_missing_package_or_long_notes_fails_before_creating_edit(self):
        self.package.unlink()
        with self.assertRaises(ValueError):
            self.submit()
        self.package.write_bytes(b"bundle")
        (self.locale / "changelogs" / "1020300.txt").write_text("x" * 501, encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "500"):
            self.submit()
        self.edits.insert.assert_not_called()


class SharedLogoTests(unittest.TestCase):
    def test_shared_brand_colors(self):
        image = render_logo(128)
        self.assertEqual(image.getpixel((64, 110)), (24, 35, 33, 255))
        self.assertEqual(image.getpixel((36, 40)), (244, 247, 245, 255))
        self.assertEqual(image.getpixel((60, 40)), (244, 247, 245, 255))
        self.assertEqual(image.getpixel((84, 40)), (238, 75, 85, 255))

    def test_transparent_corners_and_export_sizes(self):
        for size in (16, 32, 128, 1024):
            with self.subTest(size=size):
                image = render_logo(size)
                self.assertEqual(image.size, (size, size))
                self.assertEqual(image.mode, "RGBA")
                self.assertEqual(image.getpixel((0, 0))[3], 0)
                self.assertEqual(image.getpixel((size // 2, size // 2))[3], 255)

    def test_monochrome_mark_has_no_background_tile(self):
        image = render_logo(128, (255, 255, 255))
        self.assertEqual(image.getpixel((64, 110))[3], 0)
        self.assertEqual(image.getpixel((48, 40))[3], 0)
        for point in ((36, 40), (60, 40), (84, 40)):
            self.assertEqual(image.getpixel(point), (255, 255, 255, 255))


class StoreReleaseNotesTests(unittest.TestCase):
    def load_notes(self, fallback, authored=""):
        fields = {"name": "Plural", "short_description": "Record audio", "description": "Record audio tracks",
                  "release_notes": authored}
        report = Report()
        with patch("store_assets.read_text", side_effect=lambda locale, field: fields.get(field, "")), \
                patch("store_assets.read_lines", return_value=[]):
            texts = load_texts("en-US", fallback, report)
        validate("en-US", texts, report)
        return texts["release_notes"], report

    def test_long_github_release_body_is_shortened_and_passes_validation(self):
        body = "## Plural: First Release\n\n" + "Record multiple microphones with independent tracks. " * 30
        notes, report = self.load_notes(body)
        self.assertLessEqual(len(notes), LIMITS["release_notes"])
        self.assertTrue(notes.endswith("..."))
        self.assertTrue(body.startswith(notes[:-3]))
        self.assertFalse(report.errors)
        self.assertTrue(any("shortened" in warning for warning in report.warnings))

    def test_short_and_exact_limit_fallbacks_are_unchanged(self):
        for body in ("First release.", "a" * LIMITS["release_notes"]):
            with self.subTest(length=len(body)):
                notes, report = self.load_notes(body)
                self.assertEqual(notes, body)
                self.assertFalse(report.errors)
                self.assertFalse(report.warnings)

    def test_long_unbroken_fallback_also_fits(self):
        notes, report = self.load_notes("a" * 1000)
        self.assertEqual(len(notes), LIMITS["release_notes"])
        self.assertFalse(report.errors)

    def test_authored_notes_take_precedence_and_are_not_silently_truncated(self):
        notes, report = self.load_notes("GitHub body " * 100, "Store summary")
        self.assertEqual(notes, "Store summary")
        self.assertFalse(report.warnings)
        authored = "a" * (LIMITS["release_notes"] + 1)
        notes, report = self.load_notes("Short GitHub body", authored)
        self.assertEqual(notes, authored)
        self.assertTrue(any("release_notes.txt" in error for error in report.errors))

    def test_empty_fallback_keeps_default_notes(self):
        notes, report = self.load_notes("  \n")
        self.assertEqual(notes, "Bug fixes and improvements.")
        self.assertFalse(report.errors)


class MicrosoftSubmissionTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        with open(Path(__file__).resolve().parents[2] / "Marketing/store.toml", "rb") as source:
            self.cfg = tomllib.load(source)
        self.assets = self.root / "windows"
        self.package = self.root / "Plural-1.2.3.msixbundle"
        self.package.write_bytes(b"package")
        for locale in ("en-us", "de-de"):
            directory = self.assets / "listing" / locale
            (directory / "screenshots").mkdir(parents=True)
            for field, value in {"product_name": "Plural", "description": "Record voices",
                                 "short_description": "Record", "whats_new": "New release",
                                 "copyright": "2026 Plural", "product_features": "Record\nPlayback",
                                 "search_terms": "audio\nrecording"}.items():
                (directory / f"{field}.txt").write_text(value, encoding="utf-8")
            (directory / "screenshots/desktop_01.png").write_bytes(b"screenshot")
        (self.assets / "images").mkdir()
        for name in IMAGE_TYPES:
            (self.assets / "images" / name).write_bytes(b"image")
        (self.assets / "images/Poster-720x1080.png").write_bytes(b"poster")
        self.template = {
            "id": "123", "fileUploadUrl": "https://example.invalid/?sig=secret", "status": "PendingCommit",
            "pricing": {"priceId": "Tier2"}, "enterpriseLicensing": "None",
            "applicationPackages": [{"id": "old-package", "fileName": "old.msix", "fileStatus": "Uploaded"}],
            "listings": {"en-us": {"baseListing": {"images": [
                {"id": "old-image", "fileName": "old.png", "imageType": "Screenshot", "fileStatus": "Uploaded"}]}}},
        }

    def test_replaces_packages_and_images_without_mutating_template(self):
        original = copy.deepcopy(self.template)
        body, files = prepare_submission(self.template, self.cfg, self.assets, self.package)
        self.assertEqual(self.template, original)
        self.assertEqual(body["targetPublishMode"], "Immediate")
        self.assertEqual(body["pricing"]["priceId"], "Free")
        self.assertEqual(body["enterpriseLicensing"], "None")
        self.assertNotIn("fileUploadUrl", body)
        self.assertNotIn("id", body)
        self.assertEqual(body["applicationPackages"][0]["fileStatus"], "PendingDelete")
        self.assertTrue(body["applicationPackages"][1]["fileName"].endswith("/Plural-1.2.3.msixbundle"))
        self.assertEqual(set(body["listings"]), {"en-us", "de-de"})
        listing = body["listings"]["en-us"]["baseListing"]
        self.assertEqual(listing["images"][0]["fileStatus"], "PendingDelete")
        self.assertEqual(listing["features"], ["Record", "Playback"])
        self.assertNotIn("privacyPolicy", listing)
        for image in listing["images"][1:]:
            self.assertIn(image["fileName"], files)
        self.assertEqual(len(files), 9)

    def test_uploaded_images_use_documented_api_types(self):
        body, files = prepare_submission(self.template, self.cfg, self.assets, self.package)
        expected = {
            "desktop_01.png": "Screenshot",
            "StoreLogo-300x300.png": "Icon",
            "BoxArt-1080x1080.png": "StoreLogoSquare",
            "Hero-1920x1080.png": "PromotionalArt16x9",
        }
        self.assertNotIn("Poster-720x1080.png", {path.name for path in files.values()})
        for locale, listing in body["listings"].items():
            with self.subTest(locale=locale):
                uploaded = {files[image["fileName"]].name: image["imageType"]
                            for image in listing["baseListing"]["images"]
                            if image["fileStatus"] == "PendingUpload"}
                self.assertEqual(uploaded, expected)

    def test_empty_initial_draft_and_regional_unavailability_are_preserved(self):
        template = {"listings": None, "applicationPackages": None,
                    "pricing": {"priceId": "Free", "marketSpecificPricings": {"US": "Tier2", "CA": "NotAvailable"}}}
        body, files = prepare_submission(template, self.cfg, self.assets, self.package)
        self.assertEqual(len(body["applicationPackages"]), 1)
        self.assertEqual(body["pricing"]["marketSpecificPricings"], {"CA": "NotAvailable"})
        self.assertEqual(len(files), 9)

    def test_retry_uses_fresh_paths_and_discards_unuploaded_entries_without_ids(self):
        first, files = prepare_submission(self.template, self.cfg, self.assets, self.package)
        second, retry_files = prepare_submission(first, self.cfg, self.assets, self.package)
        self.assertFalse(set(files) & set(retry_files))
        self.assertEqual(len(second["applicationPackages"]), 2)
        self.assertEqual(len(second["listings"]["en-us"]["baseListing"]["images"]), 5)

    def test_real_generated_windows_listing_prepares_without_network(self):
        output = self.root / "generated"
        texts = load_texts("en-US", "Release 1.2.3", Report())
        screenshots = sorted((Path(__file__).resolve().parents[2] / "Marketing/screenshots/desktop").glob("*.png"))
        windows_listing(self.cfg, "en-US", texts, {"windows": screenshots}, render_logo(300), output)
        body, files = prepare_submission({}, self.cfg, output / "windows", self.package)
        listing = body["listings"]["en-us"]["baseListing"]
        self.assertEqual(listing["title"], texts["name"])
        self.assertEqual(sum(image["imageType"] == "Screenshot" for image in listing["images"]), len(screenshots))
        self.assertEqual(files[listing["images"][-1]["fileName"]].read_bytes()[:8], b"\x89PNG\r\n\x1a\n")

    def test_archive_contains_every_pending_upload_and_no_deleted_files(self):
        body, files = prepare_submission(self.template, self.cfg, self.assets, self.package)
        destination = self.root / "submission.zip"
        write_archive(files, destination)
        with zipfile.ZipFile(destination) as archive:
            self.assertEqual(set(archive.namelist()), set(files))
            self.assertEqual(archive.read(body["applicationPackages"][-1]["fileName"]), b"package")
            self.assertNotIn("old.png", archive.namelist())

    def test_missing_screenshots_default_locale_and_package_fail_locally(self):
        for missing in (self.assets / "listing/en-us/screenshots/desktop_01.png", self.package):
            with self.subTest(missing=missing):
                content = missing.read_bytes()
                missing.unlink()
                with self.assertRaises(ValueError):
                    prepare_submission(self.template, self.cfg, self.assets, self.package)
                missing.write_bytes(content)
        self.cfg["app"]["default_locale"] = "fr-FR"
        with self.assertRaisesRegex(ValueError, "default locale"):
            prepare_submission(self.template, self.cfg, self.assets, self.package)

    def test_publish_creates_updates_uploads_commits_and_checks_status(self):
        client = Mock()
        client.call.side_effect = [{}, self.template, {}, {}, {"status": "PreProcessing"}]
        out = self.root / "out"
        result = publish(client, self.cfg, self.assets, self.package, "1.2.3", out)
        self.assertEqual(result, ("123", "PreProcessing"))
        self.assertEqual([call.args[0] for call in client.call.call_args_list], ["GET", "POST", "PUT", "POST", "GET"])
        body = client.call.call_args_list[2].args[2]
        self.assertTrue(body["notesForCertification"].endswith("Plural CI release: 1.2.3"))
        client.upload.assert_called_once_with(self.template["fileUploadUrl"], out / "submission.zip")
        self.assertNotIn("sig=secret", (out / "submission.json").read_text(encoding="utf-8"))

    def test_reuses_any_existing_draft_without_creating_or_deleting(self):
        for note in ("Manual Partner Center draft", "Notes\n\nPlural CI release: 1.2.2"):
            with self.subTest(note=note):
                client = Mock()
                client.call.side_effect = [{"pendingApplicationSubmission": {"id": "123"}},
                                           dict(self.template, notesForCertification=note), {}, {},
                                           {"status": "Certification"}]
                result = publish(client, self.cfg, self.assets, self.package, "1.2.3", self.root / "out")
                self.assertEqual(result, ("123", "Certification"))
                self.assertEqual(client.call.call_args_list[1].args, ("GET", "/submissions/123"))
                self.assertEqual(client.call.call_args_list[2].args[:2], ("PUT", "/submissions/123"))
                self.assertFalse(any(call.args[0] == "DELETE" or call.args == ("POST", "/submissions")
                                     for call in client.call.call_args_list))

    def test_different_release_in_certification_is_not_modified(self):
        client = Mock()
        client.call.side_effect = [{"pendingApplicationSubmission": {"id": "123"}},
                                   dict(self.template, status="Certification", notesForCertification="other release")]
        with self.assertRaisesRegex(ValueError, "not an editable draft"):
            publish(client, self.cfg, self.assets, self.package, "1.2.3", self.root / "out")
        self.assertTrue(all(call.args[0] == "GET" for call in client.call.call_args_list))
        client.upload.assert_not_called()

    def test_rerun_checks_same_release_in_progress_without_uploading_again(self):
        client = Mock()
        client.call.side_effect = [{"pendingApplicationSubmission": {"id": "123"}},
                                   dict(self.template, status="Certification", notesForCertification="Notes\n\nPlural CI release: 1.2.3"),
                                   {"status": "Certification"}]
        self.assertEqual(publish(client, self.cfg, self.assets, self.package, "1.2.3", self.root / "out"),
                         ("123", "Certification"))
        client.upload.assert_not_called()
        self.assertTrue(all(call.args[0] == "GET" for call in client.call.call_args_list))

    def test_failed_update_never_uploads_or_commits(self):
        client = Mock()
        client.call.side_effect = [{}, self.template, RuntimeError("update failed")]
        with self.assertRaisesRegex(RuntimeError, "update failed"):
            publish(client, self.cfg, self.assets, self.package, "1.2.3", self.root / "out")
        client.upload.assert_not_called()
        self.assertEqual([call.args[0] for call in client.call.call_args_list], ["GET", "POST", "PUT"])

    def test_failed_upload_never_commits(self):
        client = Mock()
        client.call.side_effect = [{}, self.template, {}]
        client.upload.side_effect = RuntimeError("upload failed")
        with self.assertRaisesRegex(RuntimeError, "upload failed"):
            publish(client, self.cfg, self.assets, self.package, "1.2.3", self.root / "out")
        self.assertEqual([call.args[0] for call in client.call.call_args_list], ["GET", "POST", "PUT"])

    def test_authentication_encodes_secret_caches_and_refreshes_token(self):
        settings = {"MSSTORE_TENANT_ID": "00000000-0000-0000-0000-000000000001",
                    "MSSTORE_CLIENT_ID": "00000000-0000-0000-0000-000000000002",
                    "MSSTORE_CLIENT_SECRET": "test-secret&+=", "MSSTORE_APP_ID": "9NTEST"}
        with patch.dict("os.environ", settings, clear=True), patch("microsoft_store.SENSITIVE_VALUES", []), \
                patch("microsoft_store.time.monotonic", return_value=100) as clock, \
                patch("microsoft_store.request") as network:
            network.side_effect = [{"access_token": "test-token-one", "expires_in": 3600}, {}, {},
                                   {"access_token": "test-token-two", "expires_in": 3600}, {}]
            client = StoreClient()
            client.call("GET")
            client.call("GET")
            self.assertEqual(network.call_count, 3)
            form = parse_qs(network.call_args_list[0].args[3])
            self.assertEqual(form["client_secret"], [settings["MSSTORE_CLIENT_SECRET"]])
            self.assertEqual(form["resource"], ["https://manage.devcenter.microsoft.com"])
            clock.return_value = 5000
            client.call("GET")
            self.assertEqual(network.call_count, 5)
            self.assertEqual(network.call_args_list[-1].args[4]["Authorization"], "Bearer test-token-two")

    def test_upload_streams_file_with_length_and_without_store_bearer_token(self):
        client = StoreClient.__new__(StoreClient)

        def upload_request(host, method, path, data, headers, timeout):
            self.assertEqual((host, method, path),
                             ("storage.blob.core.windows.net", "PUT", "/upload?sig=test"))
            self.assertEqual(data.read(), b"package")
            self.assertEqual(headers["Content-Length"], str(self.package.stat().st_size))
            self.assertEqual(headers["x-ms-blob-type"], "BlockBlob")
            self.assertNotIn("Authorization", headers)
            return {}

        with patch("microsoft_store.request", side_effect=upload_request) as network, \
                patch("microsoft_store.SENSITIVE_VALUES", []):
            client.upload("https://storage.blob.core.windows.net/upload?sig=test", self.package)
            network.assert_called_once()

    def test_commit_polling_failure_and_timeout_are_not_success(self):
        client = Mock()
        client.call.side_effect = [{"status": "CommitStarted"}, {"status": "PreProcessing"}]
        with patch("microsoft_store.time.sleep"):
            self.assertEqual(wait_for_commit(client, "123"), "PreProcessing")
        for status in ("CommitFailed", "CertificationFailed", "PendingPublication", "Canceled"):
            with self.subTest(status=status):
                client.call.side_effect = None
                client.call.return_value = {"status": status, "statusDetails": {"errors": [{"code": "InvalidArchive"}]}}
                with self.assertRaises(RuntimeError):
                    wait_for_commit(client, "123")
        client.call.return_value = {"status": "CommitStarted"}
        with self.assertRaises(TimeoutError):
            wait_for_commit(client, "123", wait_seconds=0)

    def test_missing_credentials_and_untrusted_upload_urls_are_rejected(self):
        with patch.dict("os.environ", {}, clear=True):
            with self.assertRaisesRegex(ValueError, "MSSTORE_CLIENT_SECRET"):
                StoreClient()
        client = StoreClient.__new__(StoreClient)
        for url in ("http://storage.blob.core.windows.net/upload?sig=secret",
                    "https://example.invalid/upload?sig=secret",
                    "https://storage.blob.core.windows.net.attacker.invalid/upload?sig=secret"):
            with self.subTest(url=url), patch("microsoft_store.request") as network:
                with self.assertRaises(ValueError):
                    client.upload(url, self.package)
                network.assert_not_called()

    def test_diagnostics_redact_credentials_and_sas_queries(self):
        with patch("microsoft_store.SENSITIVE_VALUES", ["client-secret", "access-token"]):
            message = safe_message('client-secret access-token https://storage.blob.core.windows.net/upload?sig=secret')
        self.assertNotIn("client-secret", message)
        self.assertNotIn("access-token", message)
        self.assertNotIn("sig=", message)


if __name__ == "__main__":
    unittest.main()