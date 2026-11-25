# Stripping the ELF apphost would risk truncating the embedded .NET bundle.
%global __os_install_post %{nil}
%global _build_id_links none

Name: passload
Version: %{passload_version}
Release: 1
Summary: Temporary direct file sharing
License: MIT
URL: https://github.com/mertioz/passload
AutoReqProv: no
Requires: glibc, libgcc, libstdc++, krb5-libs, libicu, openssl-libs, zlib, ca-certificates

%description
Create temporary HTTPS download links and share selected files from this device.

%install
mkdir -p "%{buildroot}"
cp -a "%{passload_source}/." "%{buildroot}/"

%files
/usr/bin/passload
/usr/lib/passload/
/usr/share/applications/site.aymen.Passload.desktop
/usr/share/metainfo/site.aymen.Passload.metainfo.xml
/usr/share/icons/hicolor/128x128/apps/site.aymen.Passload.png
%license /usr/share/licenses/passload/LICENSE
