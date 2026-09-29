#!/bin/bash
# Run on macOS only. Creates a signed copy and an unnotarized DMG, never modifies the source app.
# Usage: bash finalize-macos.sh [GitBinder.app] [output-directory] [signing-identity-or--]
set -euo pipefail

if [[ "$(/usr/bin/uname -s)" != "Darwin" ]]; then
    printf '%s\n' 'This script requires macOS (codesign and hdiutil). No files were changed.' >&2
    exit 1
fi

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
source_app="${1:-$script_dir/GitBinder.app}"
output_dir="${2:-$script_dir}"
signing_identity="${3:--}"
entitlements="$script_dir/entitlements.plist"

if [[ ! -d "$source_app" || ! -f "$source_app/Contents/Info.plist" || ! -f "$entitlements" ]]; then
    printf '%s\n' 'Expected GitBinder.app and entitlements.plist. Check the input paths.' >&2
    exit 1
fi
source_app="$(cd -- "$source_app" && pwd -P)"
if [[ "$(basename -- "$source_app")" != "GitBinder.app" ]]; then
    printf '%s\n' 'The source bundle must be named GitBinder.app.' >&2
    exit 1
fi
for tool in /usr/bin/codesign /usr/bin/hdiutil /usr/bin/ditto /usr/bin/file /usr/bin/lipo /usr/bin/shasum; do
    if [[ ! -x "$tool" ]]; then
        printf 'Required macOS tool not available: %s\n' "$tool" >&2
        exit 1
    fi
done

version="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$source_app/Contents/Info.plist")"
if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    printf '%s\n' 'Unexpected bundle version.' >&2
    exit 1
fi
architecture="$(/usr/bin/lipo -archs "$source_app/Contents/MacOS/GitBinder.Desktop")"
case "$architecture" in
    arm64) runtime='osx-arm64' ;;
    x86_64) runtime='osx-x64' ;;
    *) printf 'Unexpected application architecture: %s\n' "$architecture" >&2; exit 1 ;;
esac

# Reject a nested output before creating directories, including paths reached through symlinks.
output_ancestor="$output_dir"
while [[ ! -d "$output_ancestor" ]]; do
    output_ancestor="$(dirname -- "$output_ancestor")"
done
output_ancestor="$(cd -- "$output_ancestor" && pwd -P)"
case "$output_ancestor" in
    "$source_app"|"$source_app"/*)
        printf '%s\n' 'Output directory must be outside the source app bundle.' >&2
        exit 1
        ;;
esac
mkdir -p -- "$output_dir"
output_dir="$(cd -- "$output_dir" && pwd -P)"
# Recheck the final physical path before creating staging or copying the bundle.
case "$output_dir" in
    "$source_app"|"$source_app"/*)
        printf '%s\n' 'Output directory must be outside the source app bundle.' >&2
        exit 1
        ;;
esac
signature_label='adhoc'
signing_options=(--force --sign "$signing_identity")
if [[ "$signing_identity" == '-' ]]; then
    signing_options+=(--timestamp=none)
else
    signature_label='developerid'
    signing_options+=(--timestamp --options runtime)
fi
dmg_name="GitBinder-$version-$runtime-$signature_label-unnotarized.dmg"
dmg_path="$output_dir/$dmg_name"
if [[ -e "$dmg_path" || -e "$dmg_path.sha256" ]]; then
    printf 'Refusing to overwrite an existing package: %s\n' "$dmg_path" >&2
    exit 1
fi

staging_dir="$(/usr/bin/mktemp -d "$output_dir/.gitbinder-macos-XXXXXX")"
image_dir="$staging_dir/image"
signed_app="$image_dir/GitBinder.app"
mkdir -p -- "$image_dir"
/usr/bin/ditto "$source_app" "$signed_app"

# Only the copied bundle is modified. Sign native libraries before app hosts, bundle last.
while IFS= read -r -d '' native_file; do
    if /usr/bin/file -b "$native_file" | /usr/bin/grep -q 'Mach-O'; then
        chmod 755 "$native_file"
        case "$native_file" in
            */GitBinder.Desktop|*/gitbinder-credential) continue ;;
        esac
        /usr/bin/codesign "${signing_options[@]}" "$native_file"
    fi
done < <(/usr/bin/find "$signed_app/Contents" -type f -print0)

for executable in GitBinder.Desktop gitbinder-credential; do
    executable_path="$signed_app/Contents/MacOS/$executable"
    if [[ ! -f "$executable_path" ]]; then
        printf 'Missing required executable: %s\n' "$executable" >&2
        exit 1
    fi
    chmod 755 "$executable_path"
    /usr/bin/codesign "${signing_options[@]}" --entitlements "$entitlements" "$executable_path"
done
/usr/bin/codesign "${signing_options[@]}" --entitlements "$entitlements" "$signed_app"
/usr/bin/codesign --verify --deep --strict --verbose=2 "$signed_app"

# --deep is used only for verification, not signing nested binaries.
ln -s /Applications "$image_dir/Applications"
/usr/bin/hdiutil create -volname 'GitBinder' -srcfolder "$image_dir" -format UDZO -fs HFS+ "$dmg_path"
if [[ "$signing_identity" != '-' ]]; then
    /usr/bin/codesign --force --timestamp --sign "$signing_identity" "$dmg_path"
fi
(
    cd -- "$output_dir"
    set -o noclobber
    /usr/bin/shasum -a 256 "$dmg_name" > "$dmg_name.sha256"
)
printf '\nCreated: %s\n' "$dmg_path"
printf 'Signature: %s; NOT notarized; macOS functional testing is still required.\n' "$signature_label"
printf 'Source app was preserved. Diagnostic staging directory retained: %s\n' "$staging_dir"
