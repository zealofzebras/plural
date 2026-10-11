# Marketing and store listings

Everything the app stores show about Plural lives in this folder. When a GitHub release is published,
[`.github/workflows/release.yml`](../.github/workflows/release.yml) builds the apps, turns these files into store
listings and images, and attaches all of it to the release.

To change the store text, edit a `.txt` file and publish a release. You don't need to touch any code.

## Files

| File | Used for | Limit |
|---|---|---|
| `store.toml` | URLs, categories, brand colours, screenshot sizes, Microsoft Store identity | |
| `brand/icon.png` | The logo (transparent, square, ≥1024 px). Every icon and tile is made from it | |
| `<locale>/name.txt` | App name: App Store, Google Play title, Microsoft Store product name | 30 |
| `<locale>/subtitle.txt` | App Store subtitle, also the tagline on the Play feature graphic | 30 |
| `<locale>/short_description.txt` | Google Play short description, Microsoft Store short description | 80 |
| `<locale>/description.txt` | Full description in every store | 4000 |
| `<locale>/promotional_text.txt` | App Store promotional text (you can change it without a new version) | 170 |
| `<locale>/keywords.txt` | App Store keywords, comma-separated with no spaces after commas. The first 7 become Microsoft Store search terms (30 characters each) | 100 |
| `<locale>/features.txt` | Microsoft Store product features, one per line | 20 × 200 |
| `<locale>/release_notes.txt` | "What's new". If empty, the GitHub release text is used | 500 |
| `<locale>/captions.txt` | Screenshot captions, one per line, in the same order as the screenshots | ~40 |
| `screenshots/phone/` | Portrait phone captures (Android) | |
| `screenshots/tablet/` | Portrait tablet captures (the phone captures are used if this is empty) | |
| `screenshots/desktop/` | Landscape desktop window captures | |

The build stops with an error if a field is over its limit, or if the name, subtitle or keywords contain
"SmartMike" or "Sabinetek". Third-party trademarks are only allowed in the description. Apple rejects them
in keywords, and Google flags them in titles.

### Adding a language

Copy `en-US/` to a folder named with the store locale code (`de-DE`, `fr-FR`, `ja`, …) and translate the files.
Every store gets the new language automatically. The screenshots are shared; only the captions are translated.

## Screenshots

Add **real captures of the app** as PNGs. Files are used in name order, so name them `01.png`, `02.png`, … and put the
most important screen first. The first two or three are all most people see in search results. For each store size,
the build frames every capture on the brand gradient with its caption above it.

- **Phone:** capture on an Android phone (power + volume down), portrait. 1080×2400 or similar works.
- **Desktop:** capture the Plural window on Windows (Win+Shift+S, window mode) at 1920×1200 or larger, using a 16:10
  window if you can. The same captures are used for the Mac App Store.
- Show the app with real content: two or three connected mics with levels moving, a recording in progress, the
  format picker. Avoid empty states.
- Keep captions short, describe the benefit, and use sentence case: "Every mic on its own track", not "Multi-track support".

Without captures, the build still finishes. It lists the missing screenshots in `REPORT.md` and writes example
layouts to `preview/` that are never packaged. Every store requires screenshots before you can submit.

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
| `Plural-1.2.3-ios.ipa` | iOS app (only once an iOS app project exists and is signed) |
| `Plural-1.2.3-<store>-store.zip` | One per store: `listing/` (text and images in that store's layout), `binaries/`, `REPORT.md` |
| `SHA256SUMS.txt` | Checksums |

The Google Play and App Store listings use the [fastlane](https://docs.fastlane.tools) `supply` and `deliver` folder
layouts, so `fastlane supply --metadata_path listing/metadata/android` or `fastlane deliver` can upload them as they
are. You can also copy them into Play Console and App Store Connect by hand. The Microsoft Store listing has one text
file per Partner Center field.

Version numbers come from the tag: `v1.2.3` gives version 1.2.3, Android versionCode / Apple build 1020300, and MSIX
1.2.3.0. Running the workflow by hand (Actions → Release → Run workflow) builds everything as run artifacts without
touching a release.

## Release workflow secrets

Everything is optional. Without secrets, the workflow still builds every app, but the results can't go to a store.
Add secrets under Settings → Secrets and variables → Actions.

| Secret | Needed for |
|---|---|
| `ANDROID_KEYSTORE_BASE64`, `ANDROID_KEYSTORE_PASSWORD`, `ANDROID_KEY_ALIAS`, `ANDROID_KEY_PASSWORD` | Signing the APK/AAB with your Play upload key (`base64 -w0 upload.keystore`) |
| `WINDOWS_CERT_PFX_BASE64`, `WINDOWS_CERT_PASSWORD` | Signing the .msixbundle for sideloading. The Store signs it for you. The certificate subject must equal the publisher |
| `MACOS_CERT_P12_BASE64`, `MACOS_CERT_PASSWORD`, `MACOS_SIGN_IDENTITY` | Developer ID signing of the macOS app (`Developer ID Application: Name (TEAMID)`) |
| `APPLE_ID`, `APPLE_TEAM_ID`, `APPLE_APP_PASSWORD` | Notarizing the macOS app (app-specific password) |
| `MAS_CERT_P12_BASE64`, `MAS_CERT_PASSWORD`, `MAS_APP_IDENTITY`, `MAS_INSTALLER_IDENTITY`, `MAS_PROVISIONING_PROFILE_BASE64` | Mac App Store `.pkg` (`Apple Distribution` and `Mac Installer Distribution` identities in one .p12) |
| `IOS_CERT_P12_BASE64`, `IOS_CERT_PASSWORD`, `IOS_SIGN_IDENTITY`, `IOS_PROVISIONING_PROFILE_BASE64` | Signed iOS `.ipa` |

| Variable | Needed for |
|---|---|
| `MSSTORE_IDENTITY_NAME`, `MSSTORE_PUBLISHER`, `MSSTORE_PUBLISHER_DISPLAY_NAME` | The MSIX identity. Copy them from Partner Center → Product identity. They override `store.toml` |

## Platform status

- **Windows, Android:** complete apps, ready for the stores.
- **macOS:** the app builds, is packaged, and can be signed and notarized. It **can't record yet**: `DesktopHost` has
  no macOS Bluetooth or audio layer (see `Docs/Plural/Design.md`, "later: Platform.MacOS"). Don't submit it to the Mac
  App Store before that exists.
- **iOS:** there is no iOS app project yet (`Src/Plural.App.iOS`), so the iOS job is skipped and only the App Store
  listing is produced. iOS only allows Bluetooth Classic serial links to MFi accessories through External Accessory,
  so check whether the SmartMike+ supports that before building an iOS app.
