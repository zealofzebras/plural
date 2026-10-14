# Marketing and store listings

Everything the app stores show about Plural lives in this folder. When a GitHub release is published,
[`.github/workflows/release.yml`](../.github/workflows/release.yml) builds the apps, turns these files into store
listings and images, and attaches all of it to the release.

To change the store text, edit a `.txt` file and publish a release. You don't need to touch any code.

## Files

| File | Used for | Limit |
|---|---|---|
| `store.toml` | URLs, categories, brand colours, screenshot sizes, Microsoft Store identity | |
| `brand/plural.svg` | Shared vector mark: three recording tracks, off-white and coral on an ink tile | |
| `brand/icon.png` | The logo (transparent, square, ≥1024 px). Every icon and tile is made from it | |
| `<locale>/name.txt` | App name: App Store, Google Play title, Microsoft Store product name | 30 |
| `<locale>/subtitle.txt` | App Store subtitle, also the tagline on the Play feature graphic | 30 |
| `<locale>/short_description.txt` | Google Play short description, Microsoft Store short description | 80 |
| `<locale>/description.txt` | Full description in every store | 4000 |
| `<locale>/promotional_text.txt` | App Store promotional text (you can change it without a new version) | 170 |
| `<locale>/keywords.txt` | App Store keywords, comma-separated with no spaces after commas. The first 7 become Microsoft Store search terms (30 characters each) | 100 |
| `<locale>/features.txt` | Microsoft Store product features, one per line | 20 × 200 |
| `<locale>/release_notes.txt` | "What's new". If empty, the GitHub release text is used, shortened to fit with a warning | 500 |
| `<locale>/captions.txt` | Screenshot captions, one per line, in the same order as the screenshots | ~40 |
| `screenshots/phone/` | Portrait phone captures (Android) | |
| `screenshots/tablet/` | Portrait tablet captures (the phone captures are used if this is empty) | |
| `screenshots/desktop/` | Landscape desktop window captures | |

The build stops with an error if a field is over its limit, or if the name, subtitle or keywords contain
"SmartMike" or "Sabinetek". Third-party trademarks are only allowed in the description. Apple rejects them
in keywords, and Google flags them in titles.

GitHub release bodies can be longer than the stores' shared 500-character limit. When used as the fallback for an
empty `release_notes.txt`, they are shortened at a word boundary with `...` and a build warning. The GitHub release
itself is unchanged. Explicitly authored `release_notes.txt` files still fail validation if they exceed the limit.

### Adding a language

Copy `en-US/` to a folder named with the store locale code (`de-DE`, `fr-FR`, `ja`, …) and translate the files.
Every store gets the new language automatically. The screenshots are shared; only the captions are translated.

## Screenshots

Screenshots are **rendered automatically** from the real app. `Src/Plural.Screenshots` runs the Plural UI without a
window (headless Avalonia) against simulated hardware:
- three SmartMike+ mics that replay a recorded mic session, so the level meters show real audio
- the device's own mics, fed with synthetic speech
- two other Plural devices for "record together"

It steps through five scenes in the order of `captions.txt`: all mics live, recording, local mics, other devices, and
recording settings. Each scene is saved as `01.png` … `05.png` in `screenshots/phone/` (1080×2376), `tablet/`
(1600×2560) and `desktop/` (2880×1800).

```powershell
./Tools/store/screenshots.ps1              # all three; or -Profile phone|tablet|desktop, -Theme light
./Tools/store/screenshots.ps1 -Preview     # also builds the framed store images into out/store
```

The release workflow renders the same screenshots on a Windows runner. **Committed screenshots win.** If a folder
here contains PNGs, the release uses them; if it's empty, the release uses freshly rendered ones. So commit the
rendered set to freeze it, replace files with hand-taken captures, or leave the folders empty to always get the
current UI. If you change the scenes in `Src/Plural.Screenshots/Program.cs`, change `captions.txt` to match.

For a hand-taken capture, use an Android phone (power + volume down) for `phone/`, and the Plural window on Windows
(Win+Shift+S, window mode, 16:10 if you can) for `desktop/`. Files are used in name order, and the first two or three
are all most people see in search results. Keep captions short, describe the benefit, and use sentence case:
"Every mic on its own track", not "Multi-track support".

For each store size, the build frames every capture on the brand gradient with its caption above it. If a folder has
no captures at all, the build still finishes. It lists the gap in `REPORT.md` and writes example layouts to
`preview/`, which are never packaged.

Sizes made from each folder (change them in `store.toml`):

| Store | Size |
|---|---|
| App Store iPhone 6.9" / 6.5" | 1320×2868, 1284×2778 |
| App Store iPad 13" | 2064×2752 |
| Mac App Store | 2880×1800 |
| Google Play phone / 7" / 10" | 1080×1920, 1200×1920, 1600×2560 |
| Microsoft Store | 3840×2160 |

Icons and graphics are made from `brand/icon.png`: the App Store icon at 1024 px with no transparency, the Play icon at
512 px, the Play feature graphic at 1024×500, the macOS icon set using Apple's icon grid, the Microsoft Store logo, box
art, poster and hero, and the MSIX tile and taskbar logos in every scale. To redraw the logo from its vector shape, run
`python Tools/store/store_assets.py logo`.

## Trying it locally

```bash
pip install pillow
python Tools/store/store_assets.py check                                   # text limits only
python -m unittest discover -s Tools/store -p test_store_assets.py          # shared logo rendering
python Tools/store/store_assets.py build --version 0.1.0 --build-number 100 --out out/store
```

## What a release produces

When you publish a release tagged `v1.2.3`, these files are attached to it:

| File | What it is |
|---|---|
| `Plural-1.2.3-windows-x64.zip`, `-windows-arm64.zip` | Portable Windows app |
| `Plural-1.2.3.msixbundle` | Windows package (x64 + arm64) for the Microsoft Store or sideloading |
| `Plural-1.2.3-android.apk` | Android app for direct install |
| `Plural-1.2.3-android.aab` | Android App Bundle for Google Play |
| `Plural-1.2.3-macos-arm64.dmg/.zip`, `-macos-x64.dmg/.zip` | macOS app |
| `Plural-1.2.3-macos-appstore.pkg` | Mac App Store upload (only when the Mac App Store secrets are set) |
| `Plural-1.2.3-ios.ipa` | iOS app (only when the iOS signing secrets are set) |
| `Plural-1.2.3-<store>-store.zip` | One per store: `listing/` (text and images in that store's layout), `binaries/`, `REPORT.md` |
| `SHA256SUMS.txt` | Checksums |

The Google Play and App Store listings use the [fastlane](https://docs.fastlane.tools) `supply` and `deliver` folder
layouts, so `fastlane supply --metadata_path listing/metadata/android` or `fastlane deliver` can upload them as they
are. You can also copy them into Play Console and App Store Connect by hand. The Microsoft Store listing has one text
file per Partner Center field.

Version numbers come from the tag: `v1.2.3` gives version 1.2.3, Android versionCode / Apple build 1020300, and MSIX
1.2.3.0. Running the workflow by hand (Actions → Release → Run workflow) builds everything as run artifacts without
touching a release or submitting to a store. GitHub prereleases also do not submit to Microsoft Store.

## Microsoft Store publishing

Stable GitHub releases automatically run the `microsoft-store` job after the Windows build and store assets succeed.
It reuses any existing editable draft (`PendingCommit`), including a draft created in Partner Center. If no pending
submission exists, it creates a new one. It never deletes submissions. An existing submission in certification or
publishing cannot be edited: a rerun of the same release checks its status; a different release fails without changing it.

Each submission replaces the Windows packages, localized names/descriptions, short descriptions, features, keywords,
release notes, copyright, screenshots, Store logos and promotional art with the generated `windows/` assets. Marketing
locale directories are authoritative for the listing languages; obsolete listing languages are removed.
API image uploads use `Screenshot`, `Icon`, `StoreLogoSquare` and `PromotionalArt16x9`. The generated 720x1080 portrait
poster remains in the store assets but is not submitted: this API has no supported image type for that size.
`store.toml` owns the category and `[microsoft.submission]` settings: free pricing, visibility, immediate publication,
hardware preferences, declarations, and certification notes. Do not claim accessibility compliance without testing it.
Age ratings, market availability and unsupported properties are retained from the existing submission.

The job uploads a ZIP to the Store-provided Azure Blob URL, commits the submission, and waits up to 20 minutes for
commit validation. Success means Microsoft accepted the submission for processing, **not** that certification passed.
Microsoft publishes automatically after certification. The job summary records the submission ID and status; the
`microsoft-submission` artifact contains the upload ZIP and JSON, with no credentials or SAS upload URLs.
Missing credentials, missing screenshots, upload failures and commit errors fail the job rather than silently skipping it.

### One-time Microsoft requirements

The [MSIX submission API](https://learn.microsoft.com/en-us/windows/uwp/monetize/manage-app-submissions) cannot complete
the age-rating questionnaire or bootstrap an app that has only a reserved name. Microsoft documents an initial
completed Partner Center submission as a prerequisite. This workflow will try an existing initial draft, but Microsoft
may reject its update or commit until the prerequisites are satisfied; it cannot bypass those service restrictions.
If CI takes over a draft, do not also edit it in Partner Center while the job is running.

The MSIX API also ignores the old `privacyPolicy`, `supportContact`, and `websiteUrl` listing fields. Set these once
on Partner Center's Properties page to the `[app]` URLs in `store.toml`. Market selection and age ratings must likewise
be configured in Partner Center; the workflow preserves them. Pricing Version 2 is not supported by this API's pricing
module, so accounts using it must retain pricing in Partner Center instead of sending `[microsoft.submission.pricing]`.

Associate an Entra tenant with the Partner Center account and add an Entra application with the **Manager** role
(scope it to Plural where available). Add `MSSTORE_APP_ID`, `MSSTORE_TENANT_ID`, `MSSTORE_CLIENT_ID` as GitHub Actions
variables and `MSSTORE_CLIENT_SECRET` as a secret. The app ID is the Store ID (for example `9N...`), not the package
identity or Entra client ID. Use the client secret **value**, not its ID, and rotate it before expiry. The three package
identity variables below remain required for the build. No signing certificate is needed for Store distribution.

To prepare and inspect a submission without authentication or uploads:

```sh
python Tools/store/microsoft_store.py --assets out/store/windows --package out/dist/Plural-1.2.3.msixbundle --version 1.2.3 --prepare-only
```

Store tooling tests: `python -m unittest discover -s Tools/store -p test_store_assets.py -v`.

## Release workflow secrets

Signing secrets are optional for building. Stable releases require the Microsoft Store credentials for submission;
without them the submission job fails, while the other jobs still produce their artifacts.
Add secrets under Settings → Secrets and variables → Actions.

| Secret | Needed for |
|---|---|
| `MSSTORE_CLIENT_SECRET` | Entra application client secret value for automated Microsoft Store submission |
| `ANDROID_KEYSTORE_BASE64`, `ANDROID_KEYSTORE_PASSWORD`, `ANDROID_KEY_ALIAS`, `ANDROID_KEY_PASSWORD` | Signing the APK/AAB with your Play upload key (`base64 -w0 upload.keystore`) |
| `WINDOWS_CERT_PFX_BASE64`, `WINDOWS_CERT_PASSWORD` | Signing the .msixbundle for sideloading. The Store signs it for you. The certificate subject must equal the publisher |
| `MACOS_CERT_P12_BASE64`, `MACOS_CERT_PASSWORD`, `MACOS_SIGN_IDENTITY` | Developer ID signing of the macOS app (`Developer ID Application: Name (TEAMID)`) |
| `APPLE_ID`, `APPLE_TEAM_ID`, `APPLE_APP_PASSWORD` | Notarizing the macOS app (app-specific password) |
| `MAS_CERT_P12_BASE64`, `MAS_CERT_PASSWORD`, `MAS_APP_IDENTITY`, `MAS_INSTALLER_IDENTITY`, `MAS_PROVISIONING_PROFILE_BASE64` | Mac App Store `.pkg` (`Apple Distribution` and `Mac Installer Distribution` identities in one .p12) |
| `IOS_CERT_P12_BASE64`, `IOS_CERT_PASSWORD`, `IOS_SIGN_IDENTITY`, `IOS_PROVISIONING_PROFILE_BASE64` | Signed iOS `.ipa` |

| Variable | Needed for |
|---|---|
| `MSSTORE_IDENTITY_NAME`, `MSSTORE_PUBLISHER`, `MSSTORE_PUBLISHER_DISPLAY_NAME` | The MSIX identity. Copy them from Partner Center → Product identity. They override `store.toml` |
| `MSSTORE_APP_ID` | Microsoft Store ID of Plural, from Partner Center Product identity |
| `MSSTORE_TENANT_ID`, `MSSTORE_CLIENT_ID` | Entra tenant and application IDs associated with Partner Center |

## Platform status

- **Windows, Android:** complete apps, ready for the stores.
- **macOS:** the app builds, is packaged, and can be signed and notarized. `Plural.Platform.MacOS` provides
  Bluetooth (IOBluetooth RFCOMM), audio output and local mics (Core Audio), and AAC; there is no camera yet. It is
  **not yet tested on a Mac**: run it with a SmartMike before submitting to the Mac App Store.
- **iOS:** `Src/Plural.App.iOS` builds in the iOS job (an `.ipa` only with the signing secrets). Local mics, recording,
  sharing and Wi-Fi remote mode are there; no camera yet. iOS only lets apps reach Bluetooth Classic accessories that are
  MFi through External Accessory, so SmartMikes work on iOS only if the SmartMike+ is an MFi accessory and its protocol
  string is added to `Src/Plural.App.iOS/Info.plist`. The app's error message lists the protocols a connected mic offers.
  Wi-Fi discovery also needs Apple's multicast entitlement (see `Src/Plural.App.iOS/Entitlements.plist`).
