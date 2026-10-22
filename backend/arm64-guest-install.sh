#!/bin/sh
# Executed via authenticated Reims SSH. No sudo, writes only under HOME.
set -eu
umask 077
token="${1:-}"
case "$token" in
  reims-arm64-transfer-*) ;;
  *) echo "Invalid transfer identifier" >&2; exit 1 ;;
esac
case "$token" in
  *[!a-zA-Z0-9-]*|'') echo "Invalid transfer identifier" >&2; exit 1 ;;
esac
if [ "$(uname -m)" != "x86_64" ]; then
  echo "ARM64 compatibility requires an Intel macOS guest" >&2
  exit 1
fi
incoming="$HOME/.$token"
test -f "$incoming/runtime.zip" && test -f "$incoming/sysroot.zip" || {
  echo "Missing uploaded archives" >&2; exit 1;
}
parent="$HOME/.local/share"
base="$parent/reims-arm64"
stage="$parent/.reims-arm64-stage-$token"
previous="$parent/.reims-arm64-previous-$token"
mkdir -p "$parent" "$stage/runtime" "$stage/sysroot"
cleanup() { rm -rf "$stage" "$incoming"; }
trap cleanup EXIT HUP INT TERM
/usr/bin/unzip -qq "$incoming/runtime.zip" -d "$stage/runtime"
/usr/bin/unzip -qq "$incoming/sysroot.zip" -d "$stage/sysroot"
test -s "$stage/runtime/maclator"
test -s "$stage/runtime/gpu/libmclbridge.dylib"
test -s "$stage/runtime/gpu/libmclmetal.dylib"
test -s "$stage/sysroot/dyld"
test -s "$stage/sysroot/dyld_shared_cache_arm64e"
test -f "$stage/runtime/SHA256SUMS"
test -f "$stage/sysroot/SHA256SUMS"
(cd "$stage/runtime" && /usr/bin/shasum -a 256 -c SHA256SUMS)
(cd "$stage/sysroot" && /usr/bin/shasum -a 256 -c SHA256SUMS)
/usr/bin/lipo -archs "$stage/runtime/maclator" | /usr/bin/grep -qw x86_64
/usr/bin/lipo -archs "$stage/runtime/gpu/libmclbridge.dylib" | /usr/bin/grep -qw x86_64
/usr/bin/lipo -archs "$stage/runtime/gpu/libmclmetal.dylib" | /usr/bin/grep -qw arm64
/usr/bin/lipo -archs "$stage/sysroot/dyld" | /usr/bin/grep -Eq 'arm64|arm64e'
chmod 755 "$stage/runtime/maclator"
if [ -e "$previous" ]; then
  echo "Previous interrupted installation requires manual inspection" >&2
  exit 1
fi
had_previous=no
if [ -d "$base" ]; then mv "$base" "$previous"; had_previous=yes; fi
if ! mv "$stage" "$base"; then
  if [ "$had_previous" = yes ]; then mv "$previous" "$base"; fi
  echo "Failed to activate ARM64 compatibility" >&2
  exit 1
fi
mkdir -p "$HOME/bin"
wrapper="$HOME/bin/.reims-arm64-$token"
cat > "$wrapper" <<'WRAPPER'
#!/bin/sh
set -eu
base="$HOME/.local/share/reims-arm64"
exec "$base/runtime/maclator" --sysroot "$base/sysroot" --dyld "$base/sysroot/dyld" "$@"
WRAPPER
chmod 755 "$wrapper"
mv -f "$wrapper" "$HOME/bin/reims-arm64"
if [ "$had_previous" = yes ]; then rm -rf "$previous"; fi
echo "Installed Reims ARM64 compatibility: ~/bin/reims-arm64"
echo "Example: ~/bin/reims-arm64 /path/to/App.app/Contents/MacOS/Executable"
