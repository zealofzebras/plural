#!/usr/bin/env python3
"""Prepare and publish MSIX submissions from the generated Windows store assets."""
from __future__ import annotations

import argparse
import copy
import http.client
import json
import os
from pathlib import Path
import re
import sys
import time
import tomllib
from urllib.parse import urlencode, urlsplit
from uuid import UUID, uuid4
import zipfile


ROOT = Path(__file__).resolve().parents[2]
API_HOST = "manage.devcenter.microsoft.com"
CI_MARKER = "Plural CI release: "
ACCEPTED_STATUSES = {"PreProcessing", "Certification", "Release", "Publishing", "Published"}
SENSITIVE_VALUES: list[str] = []
IMAGE_TYPES = {
    "StoreLogo-300x300.png": "Icon",
    "BoxArt-1080x1080.png": "StoreLogoSquare",
    "Hero-1920x1080.png": "PromotionalArt16x9",
}
TEXT_FIELDS = {
    "title": "product_name",
    "description": "description",
    "shortDescription": "short_description",
    "releaseNotes": "whats_new",
    "copyrightAndTrademarkInfo": "copyright",
}


def prepare_submission(template: dict, cfg: dict, assets: Path, package: Path) -> tuple[dict, dict]:
    if not package.is_file() or package.suffix != ".msixbundle":
        raise ValueError("An existing .msixbundle is required")
    listing_dirs = sorted(path for path in (assets / "listing").iterdir() if path.is_dir())
    if not listing_dirs or cfg["app"]["default_locale"].lower() not in {p.name for p in listing_dirs}:
        raise ValueError("Windows listings must include the default locale")

    body = copy.deepcopy(template)
    body.update(cfg["microsoft"]["submission"])
    if "pricing" in cfg["microsoft"]["submission"]:
        unavailable = {market: price for market, price in
                       (template.get("pricing") or {}).get("marketSpecificPricings", {}).items()
                       if price == "NotAvailable"}
        unavailable.update(body["pricing"].get("marketSpecificPricings", {}))
        body["pricing"] = dict(body["pricing"], marketSpecificPricings=unavailable)
    body["applicationCategory"] = cfg["categories"]["microsoft"]
    body["listings"] = {}
    upload_id = uuid4().hex
    files = {f"packages/{upload_id}/{package.name}": package}
    for directory in listing_dirs:
        def text(field: str) -> str:
            return (directory / f"{field}.txt").read_text(encoding="utf-8").strip()

        listing = {field: text(source) for field, source in TEXT_FIELDS.items()}
        for field in ("title", "description"):
            if not listing[field]:
                raise ValueError(f"{directory.name}: {field} is required")
        listing["features"] = text("product_features").splitlines()
        listing["keywords"] = text("search_terms").splitlines()
        listing["licenseTerms"] = cfg["microsoft"].get("license_terms", "")
        old_listing = (template.get("listings") or {}).get(directory.name, {}).get("baseListing") or {}
        images = [dict(image, fileStatus="PendingDelete") for image in old_listing.get("images") or []
              if image.get("id")]
        screenshots = sorted((directory / "screenshots").glob("*.png"))
        if not 1 <= len(screenshots) <= 10:
            raise ValueError(f"{directory.name}: between 1 and 10 desktop screenshots are required")
        for image in screenshots + [assets / "images" / name for name in IMAGE_TYPES]:
            if not image.is_file():
                raise ValueError(f"Missing store image: {image.name}")
            archive_name = f"images/{upload_id}/{directory.name}/{image.name}"
            files[archive_name] = image
            images.append({"fileName": archive_name, "fileStatus": "PendingUpload",
                           "imageType": IMAGE_TYPES.get(image.name, "Screenshot"), "description": ""})
        listing["images"] = images
        body["listings"][directory.name] = {"baseListing": listing, "platformOverrides": {}}
    body["applicationPackages"] = [dict(package_info, fileStatus="PendingDelete")
                                   for package_info in template.get("applicationPackages") or []
                                   if package_info.get("id")]
    body["applicationPackages"].append({"fileName": next(iter(files)), "fileStatus": "PendingUpload",
                                        "minimumDirectXVersion": "None", "minimumSystemRam": "None"})
    for field in ("id", "status", "statusDetails", "fileUploadUrl", "friendlyName"):
        body.pop(field, None)
    return body, files


def write_archive(files: dict, destination: Path) -> None:
    destination.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(destination, "w", compression=zipfile.ZIP_DEFLATED, allowZip64=True) as archive:
        for name, path in files.items():
            archive.write(path, name)


def protect(value: str) -> None:
    SENSITIVE_VALUES.append(value)
    if os.environ.get("GITHUB_ACTIONS") == "true":
        escaped = value.replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A")
        print(f"::add-mask::{escaped}")


def safe_message(value: str) -> str:
    for secret in SENSITIVE_VALUES:
        value = value.replace(secret, "[redacted]")
    return re.sub(r"https?://[^\s\"<>]+", lambda match: match[0].split("?")[0], value)


def request(host: str, method: str, path: str, data=None, headers=None, timeout=120) -> dict:
    connection = http.client.HTTPSConnection(host, timeout=timeout)
    try:
        connection.request(method, path, body=data, headers=headers or {})
        response = connection.getresponse()
        content = response.read()
        if not 200 <= response.status < 300:
            correlation = response.getheader("MS-CorrelationId", "unavailable")
            detail = safe_message(content.decode("utf-8", errors="replace")) if host == API_HOST else ""
            raise RuntimeError(f"{method} {host}: HTTP {response.status}, correlation {correlation}. {detail}")
        return json.loads(content) if content else {}
    finally:
        connection.close()


class StoreClient:
    def __init__(self):
        names = ("MSSTORE_TENANT_ID", "MSSTORE_CLIENT_ID", "MSSTORE_CLIENT_SECRET", "MSSTORE_APP_ID")
        missing = [name for name in names if not os.environ.get(name)]
        if missing:
            raise ValueError("Missing GitHub Actions settings: " + ", ".join(missing))
        self.tenant, self.client, self.secret, app_id = (os.environ[name] for name in names)
        UUID(self.tenant)
        UUID(self.client)
        if not re.fullmatch(r"[A-Za-z0-9]+", app_id):
            raise ValueError("MSSTORE_APP_ID must be the Store ID, not the package identity or Entra client ID")
        protect(self.secret)
        self.app_path = f"/v1.0/my/applications/{app_id}"
        self.token = ""
        self.token_expires = 0.0

    def call(self, method: str, suffix: str = "", body=None) -> dict:
        if time.monotonic() >= self.token_expires:
            form = urlencode({"grant_type": "client_credentials", "client_id": self.client,
                              "client_secret": self.secret, "resource": f"https://{API_HOST}"})
            token = request("login.microsoftonline.com", "POST", f"/{self.tenant}/oauth2/token", form,
                            {"Content-Type": "application/x-www-form-urlencoded"})
            self.token = token["access_token"]
            protect(self.token)
            self.token_expires = time.monotonic() + int(token.get("expires_in", 3600)) - 120
        return request(API_HOST, method, self.app_path + suffix,
                       json.dumps(body).encode("utf-8") if body is not None else None,
                       {"Authorization": f"Bearer {self.token}", "Content-Type": "application/json"})

    def upload(self, url: str, archive: Path) -> None:
        parsed = urlsplit(url)
        if (parsed.scheme != "https" or not parsed.hostname or
                not parsed.hostname.endswith(".blob.core.windows.net") or parsed.username or
                parsed.port not in (None, 443) or not parsed.query):
            raise ValueError("The Store did not provide a valid Azure Blob HTTPS upload URL")
        protect(url)
        with archive.open("rb") as data:
            request(parsed.hostname, "PUT", parsed.path + "?" + parsed.query, data,
                    {"Content-Type": "application/zip", "Content-Length": str(archive.stat().st_size),
                     "x-ms-blob-type": "BlockBlob", "x-ms-version": "2023-11-03"}, timeout=1800)


def wait_for_commit(client, submission_id: str, wait_seconds: int = 1200) -> str:
    deadline = time.monotonic() + wait_seconds
    while True:
        result = client.call("GET", f"/submissions/{submission_id}/status")
        status = result["status"]
        print(f"Microsoft Store submission {submission_id}: {status}")
        if status.endswith("Failed") or status == "Canceled":
            raise RuntimeError(safe_message(f"Store submission {status}: {json.dumps(result.get('statusDetails', {}))}"))
        if status in ACCEPTED_STATUSES:
            return status
        if status not in {"PendingCommit", "CommitStarted"}:
            raise RuntimeError(f"Unexpected Store status {status}; submission {submission_id} needs attention")
        if time.monotonic() >= deadline:
            raise TimeoutError(f"Commit validation timed out for submission {submission_id}; rerun to check its status")
        time.sleep(30)


def publish(client, cfg: dict, assets: Path, package: Path, version: str, out: Path) -> tuple[str, str]:
    prepare_submission({}, cfg, assets, package)
    application = client.call("GET")
    pending = application.get("pendingApplicationSubmission")
    if pending:
        template = client.call("GET", f"/submissions/{pending['id']}")
        if template["status"] != "PendingCommit":
            if template.get("notesForCertification", "").endswith(CI_MARKER + version):
                return pending["id"], wait_for_commit(client, pending["id"])
            raise ValueError(f"Submission {pending['id']} is {template['status']}, not an editable draft. "
                             "It has been left untouched; retry after it completes.")
        print(f"Reusing Microsoft Store draft {pending['id']}")
    else:
        template = client.call("POST", "/submissions")
        print(f"Created Microsoft Store submission {template['id']}")
    submission_id = template["id"]
    body, files = prepare_submission(template, cfg, assets, package)
    body["notesForCertification"] += "\n\n" + CI_MARKER + version
    write_archive(files, out / "submission.zip")
    (out / "submission.json").write_text(json.dumps(body, indent=2, ensure_ascii=False), encoding="utf-8")
    client.call("PUT", f"/submissions/{submission_id}", body)
    client.upload(template["fileUploadUrl"], out / "submission.zip")
    client.call("POST", f"/submissions/{submission_id}/commit")
    return submission_id, wait_for_commit(client, submission_id)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--assets", type=Path, required=True, help="Generated out/store/windows directory")
    parser.add_argument("--package", type=Path, required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--out", type=Path, default=Path("out/microsoft-submission"))
    parser.add_argument("--prepare-only", action="store_true", help="Build the upload ZIP and JSON without Store calls")
    args = parser.parse_args()
    if not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+", args.version):
        parser.error("--version must have the form 1.2.3")
    with (ROOT / "Marketing/store.toml").open("rb") as source:
        cfg = tomllib.load(source)
    args.out.mkdir(parents=True, exist_ok=True)
    if args.prepare_only:
        body, files = prepare_submission({}, cfg, args.assets, args.package)
        write_archive(files, args.out / "submission.zip")
        (args.out / "submission.json").write_text(json.dumps(body, indent=2, ensure_ascii=False), encoding="utf-8")
        print(f"Prepared Microsoft Store submission in {args.out}; nothing uploaded")
        return
    submission_id, status = publish(StoreClient(), cfg, args.assets, args.package, args.version, args.out)
    summary = (f"## Microsoft Store\n\nVersion {args.version}, submission {submission_id}: **{status}**.\n"
               "Commit accepted. Automatic publication follows Microsoft's certification; this is not a certification pass.\n")
    print(summary)
    if os.environ.get("GITHUB_STEP_SUMMARY"):
        with open(os.environ["GITHUB_STEP_SUMMARY"], "a", encoding="utf-8") as destination:
            destination.write(summary)


if __name__ == "__main__":
    try:
        main()
    except (ValueError, RuntimeError, OSError, KeyError, http.client.HTTPException) as error:
        print(safe_message(f"Microsoft Store publishing failed: {error}"), file=sys.stderr)
        sys.exit(1)