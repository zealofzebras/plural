"""Submit the generated Android listing and signed AAB using Application Default Credentials."""

import argparse
import os
from pathlib import Path


IMAGE_TYPES = ("icon", "featureGraphic", "phoneScreenshots", "sevenInchScreenshots", "tenInchScreenshots")
PACKAGE_NAME = "net.getplural.app"


def publish(service, assets, package, build_number, version, track, status, media_upload):
    metadata = assets / "metadata" / "android"
    locales = sorted(path for path in metadata.iterdir() if path.is_dir())
    if not package.is_file() or not locales:
        raise ValueError("A signed AAB and at least one listing locale are required")
    listings = []
    notes = []
    for locale in locales:
        listing = {field: (locale / filename).read_text(encoding="utf-8").strip()
                   for field, filename in (("title", "title.txt"), ("shortDescription", "short_description.txt"),
                                           ("fullDescription", "full_description.txt"))}
        if not all(listing.values()):
            raise ValueError(f"Incomplete listing: {locale.name}")
        text = (locale / "changelogs" / f"{build_number}.txt").read_text(encoding="utf-8").strip()
        if len(text) > 500:
            raise ValueError(f"Release notes exceed 500 characters: {locale.name}")
        listings.append((locale, listing))
        if text:
            notes.append({"language": locale.name, "text": text})

    edits = service.edits()
    edit_id = edits.insert(packageName=PACKAGE_NAME, body={}).execute()["id"]
    context = {"packageName": PACKAGE_NAME, "editId": edit_id}
    try:
        upload = edits.bundles().upload(
            **context, media_body=media_upload(str(package), mimetype="application/octet-stream", resumable=True))
        bundle = None
        while bundle is None:
            _, bundle = upload.next_chunk(num_retries=3)
        if int(bundle["versionCode"]) != build_number:
            raise ValueError("Uploaded AAB versionCode does not match the release build number")
        for locale, listing in listings:
            edits.listings().update(**context, language=locale.name, body=listing).execute(num_retries=3)
            for image_type in IMAGE_TYPES:
                location = locale / "images" / image_type
                images = [location.with_suffix(".png")] if location.with_suffix(".png").is_file() else (
                    sorted(location.glob("*.png")) if location.is_dir() else [])
                if not images:
                    continue
                image_context = {**context, "language": locale.name, "imageType": image_type}
                edits.images().deleteall(**image_context).execute(num_retries=3)
                for image in images:
                    edits.images().upload(**image_context, media_body=media_upload(
                        str(image), mimetype="image/png")).execute(num_retries=3)
        release = {"name": version, "versionCodes": [str(build_number)], "status": status, "releaseNotes": notes}
        edits.tracks().update(**context, track=track, body={"track": track, "releases": [release]}).execute(num_retries=3)
        edits.validate(**context).execute(num_retries=3)
        edits.commit(**context).execute()
    except Exception:
        try:
            edits.delete(**context).execute()
        except Exception:
            pass
        raise
    return edit_id


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--assets", type=Path, required=True)
    parser.add_argument("--package", type=Path, required=True)
    parser.add_argument("--build-number", type=int, required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--track", default="internal")
    parser.add_argument("--status", choices=("draft", "completed"), default="completed")
    args = parser.parse_args()

    import google.auth
    from googleapiclient.discovery import build
    from googleapiclient.http import MediaFileUpload

    credentials, _ = google.auth.default(scopes=["https://www.googleapis.com/auth/androidpublisher"])
    with build("androidpublisher", "v3", credentials=credentials, cache_discovery=False) as service:
        edit_id = publish(service, args.assets, args.package, args.build_number, args.version,
                          args.track, args.status, MediaFileUpload)
    summary = f"Google Play: version {args.version}, track {args.track}, status {args.status}, edit {edit_id}."
    print(summary)
    if os.environ.get("GITHUB_STEP_SUMMARY"):
        with open(os.environ["GITHUB_STEP_SUMMARY"], "a", encoding="utf-8") as output:
            output.write(summary + "\n")


if __name__ == "__main__":
    main()