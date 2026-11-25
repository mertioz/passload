#!/usr/bin/env bash
set -euo pipefail

rid=$1
binary=$2
out=$3
version=$("$binary" --version)
source_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
license="$source_dir/../../LICENSE"
work=$(mktemp -d "${TMPDIR:-/tmp}/passload-package.XXXXXX")
trap 'rm -rf -- "$work"' EXIT
mkdir -p "$out" "$work/archive"
install -m 0755 "$binary" "$work/archive/passload"
install -m 0644 "$license" "$work/archive/LICENSE"
tar -czf "$out/passload-$rid.tar.gz" -C "$work/archive" passload LICENSE

if [[ "$rid" == osx-* ]]; then
  stage="$work/pkg"
  mkdir -p "$stage/bin" "$stage/libexec/passload" "$stage/share/licenses/passload"
  install -m 0755 "$binary" "$stage/libexec/passload/passload"
  install -m 0644 "$license" "$stage/share/licenses/passload/LICENSE"
  ln -s ../libexec/passload/passload "$stage/bin/passload"
  pkgbuild --root "$stage" --identifier site.aymen.passload --version "$version" \
    --install-location /usr/local --ownership recommended "$out/passload-$rid.pkg"
  exit
fi

case "$rid" in
  linux-x64) deb_arch=amd64; rpm_arch=x86_64 ;;
  linux-arm64) deb_arch=arm64; rpm_arch=aarch64 ;;
  *) echo "Architecture de paquet inconnue" >&2; exit 1 ;;
esac

stage="$work/deb"
install -Dm0755 "$binary" "$stage/usr/lib/passload/passload"
install -Dm0644 "$source_dir/site.aymen.Passload.desktop" "$stage/usr/share/applications/site.aymen.Passload.desktop"
install -Dm0644 "$source_dir/site.aymen.Passload.metainfo.xml" "$stage/usr/share/metainfo/site.aymen.Passload.metainfo.xml"
install -Dm0644 "$source_dir/site.aymen.Passload.png" "$stage/usr/share/icons/hicolor/128x128/apps/site.aymen.Passload.png"
install -Dm0644 "$license" "$stage/usr/share/doc/passload/copyright"
mkdir -p "$stage/usr/bin" "$stage/DEBIAN"
ln -s ../lib/passload/passload "$stage/usr/bin/passload"
printf 'deb\n' > "$stage/usr/lib/passload/package-format"
cat > "$stage/DEBIAN/control" <<EOF
Package: passload
Version: $version
Architecture: $deb_arch
Section: net
Priority: optional
Maintainer: mertioz <mertioz@users.noreply.github.com>
Depends: libc6, libgcc-s1, libstdc++6, libgssapi-krb5-2, zlib1g, libicu70 | libicu72 | libicu74 | libicu76 | libicu78, libssl3t64 | libssl3, ca-certificates
Homepage: https://github.com/mertioz/passload
Description: Temporary direct file sharing
 Create temporary HTTPS download links for the files you choose to share.
EOF
dpkg-deb --root-owner-group --build "$stage" "$out/passload-$rid.deb"

rpm_stage="$work/rpm-files"
mkdir -p "$rpm_stage"
cp -a "$stage/usr" "$rpm_stage/"
printf 'rpm\n' > "$rpm_stage/usr/lib/passload/package-format"
install -Dm0644 "$license" "$rpm_stage/usr/share/licenses/passload/LICENSE"
rm -- "$rpm_stage/usr/share/doc/passload/copyright"
rpmbuild -bb --target "$rpm_arch" --define "_topdir $work/rpm" \
  --define "passload_source $rpm_stage" --define "passload_version $version" "$source_dir/passload.spec"
cp "$work/rpm/RPMS/$rpm_arch/passload-$version-1.$rpm_arch.rpm" "$out/passload-$rid.rpm"
