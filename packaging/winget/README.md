# winget manifests

Each released version has a folder `MAYSKIY.Aeris/<version>` with three manifests: version, installer and the en-US locale. They are kept here and copied to [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs) after a release.

## After a new release

1. Copy the folder of the previous version to `MAYSKIY.Aeris/<new version>`.
2. Replace the version in all three files, the installer URL, `ReleaseDate` and `ReleaseNotesUrl`.
3. Set `InstallerSha256` to the hash of the setup file attached to the GitHub release. Take it from the release's `SHA256SUMS.txt`, not from a local build: a local build produces a different file.
4. Check the manifests:

   ```powershell
   winget validate --manifest packaging/winget/MAYSKIY.Aeris/<version>
   ```

5. Fork `microsoft/winget-pkgs`, put the folder in `manifests/m/MAYSKIY/Aeris/<version>` and open a pull request. The winget bot runs its own checks and a malware scan; a maintainer merges it after that.

`wingetcreate update MAYSKIY.Aeris --version <version> --urls <setup url> --submit` does steps 1–5 in one go if you have [wingetcreate](https://github.com/microsoft/winget-create) installed.

`ProductCode` is the Inno Setup `AppId` with the `_is1` suffix. It must not change between versions, otherwise winget treats an upgrade as a different app.
