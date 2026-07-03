%global _binaries_in_noarch_packages_terminate_build 0
%global __os_install_post %{nil}
%global debug_package %{nil}

Name:           justdownload
Version:        %{getenv:JD_VERSION}
Release:        1%{?dist}
Summary:        Extremely light & fast cross-platform download manager
License:        MIT
URL:            https://github.com/sarfraznawaz2005/jd
BuildArch:      x86_64
Requires:       dotnet-runtime-8.0
# The tree at %{getenv:JD_STAGE} is already a built, framework-dependent + ReadyToRun publish output
# (build/build-linux-packages.ps1, docs/publishing.md) — there is no source to compile, so this spec
# skips %prep/%build entirely and just stages the prebuilt tree into %{buildroot} in %install.

%description
JustDownload is a free, open-source download manager with dynamic segmentation, pause/resume with crash
recovery, HLS support, and a browser extension. This package installs the app to /opt/justdownload with a
/usr/bin/justdownload launcher, desktop entry, and icons.

%install
rm -rf %{buildroot}
mkdir -p %{buildroot}
cp -a %{getenv:JD_STAGE}/. %{buildroot}/

%files
/opt/justdownload
/usr/bin/justdownload
/usr/share/applications/justdownload.desktop
/usr/share/icons/hicolor/*/apps/justdownload.png

%post
update-desktop-database -q /usr/share/applications >/dev/null 2>&1 || :
gtk-update-icon-cache -q /usr/share/icons/hicolor >/dev/null 2>&1 || :

%preun
# $1 == 0 means this is the final removal (not an upgrade) — mirrors the Windows MSI's uninstall
# custom action and the .deb's prerm: deregister native-messaging manifests while the binary still exists.
if [ "$1" = "0" ]; then
    /opt/justdownload/JustDownload.App --uninstall-cleanup || :
fi

%changelog
* Mon Jan 01 2026 JustDownload contributors <noreply@justdownload.app> - 1.0.0-1
- Initial Linux rpm packaging (TASK-078)
