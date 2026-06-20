# Releasing Dispatch SMTP Relay

Releases are built and published automatically by [`.github/workflows/release.yml`](../.github/workflows/release.yml)
when you push a semver tag.

## Cut a release

```bash
# from a green main (or the release branch), pick the next version
git tag v1.0.0
git push origin v1.0.0
```

That triggers the **Release** workflow, which:

1. Stamps the version (`v1.0.0` → `1.0.0`) into the published service, the MSI, and the Burn bundle.
2. Builds the **Windows installer** (`DispatchSetup-<ver>-x64.exe` + `Dispatch-<ver>-x64.msi`) and, if signing is configured,
   Authenticode-signs both (engine detached, signed, reattached, then the bundle is signed).
3. Builds the **universal Linux tarball** (`dispatch-<ver>-linux.tar.gz`, x64 + arm64, self-contained) —
   `install.sh` auto-detects the arch; no .NET SDK needed on the box.
4. Generates `SHA256SUMS` and publishes a **GitHub Release** with all four assets and auto-generated notes.

### Versioning

- Use `vMAJOR.MINOR.PATCH` (e.g. `v1.2.3`).
- A version containing a hyphen (e.g. `v1.2.3-rc1`) is published as a **pre-release**.
- The MSI uses `MajorUpgrade`, so bumping the version lets an installed instance upgrade in place.

### Dry run (no tag)

Run the **Release** workflow manually (`workflow_dispatch`) with a `version` input. It builds everything
and publishes a **draft** release you can inspect and delete — nothing goes public.

## What users download

| Platform | Asset | Notes |
|----------|-------|-------|
| Windows  | **`DispatchSetup-<ver>-x64.exe`** | The single file. Installs SQL Server 2025 Express → the MSI → the service. |
| Windows  | `Dispatch-<ver>-x64.msi` | Advanced: install against an existing SQL Server (`msiexec /i Dispatch-<ver>-x64.msi SQLCONN="..."`). |
| Linux    | `dispatch-<ver>-linux.tar.gz` | Universal (x64 + arm64). Extract, then `sudo ./install.sh --install-sql ...` (auto-detects arch). |
| Any      | `ghcr.io/chrismuench/dispatch-smtp-relay:<ver>` | Multi-arch (amd64+arm64) container image; pushed to GHCR by the `docker` job. |
| All      | `SHA256SUMS` | Verify downloads: `sha256sum -c SHA256SUMS`. |

## First release — one-time setup

The first tag push creates the GHCR package, but **GHCR packages are private by default**, so anonymous
`docker pull` will fail until you make it public:

1. Push the first tag (e.g. `v1.0.0`) and let the **Release** workflow's `docker` job publish the image.
2. Go to the repo's **Packages** → `dispatch-smtp-relay` → **Package settings** → **Change visibility → Public**
   (and, optionally, **Connect repository** so the package shows on the repo page).
3. Verify anonymous pull works on a clean machine:
   ```bash
   docker pull ghcr.io/chrismuench/dispatch-smtp-relay:1.0.0
   ```

Subsequent releases reuse the same (now public) package — this step is only needed once.

## Code signing (Azure Artifact Signing)

Signing is **opt-in and dormant** until provisioned — until then releases publish unsigned (Windows
SmartScreen will warn on first run). To enable it:

1. Set up an **Azure Artifact Signing** account + certificate profile (renamed from Trusted Signing,
   Jan 2026; ~$9.99/mo). Create a certificate profile and note the account name, profile name, and the
   regional endpoint (e.g. `https://eus.codesigning.azure.net/`).
2. Create a federated (OIDC) Azure service principal and grant it the **Artifact Signing Certificate
   Profile Signer** role on the account.
3. In the GitHub repo settings add:
   - **Variables:** `AZURE_SIGNING_ENDPOINT`, `AZURE_SIGNING_ACCOUNT`, `AZURE_CERT_PROFILE`
   - **Secrets:** `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`

The next tagged release then signs the MSI and the bundle automatically. The signing steps key off
`AZURE_SIGNING_ACCOUNT`; if it's unset, they're skipped.

> Note: the project is licensed AGPL-3.0 + Commons Clause. The Commons Clause is not OSI-approved, so the
> free **SignPath Foundation** OSS signing program does not apply — Azure Artifact Signing (a paid service
> tied to your own identity, license-agnostic) is the path here.

## CI vs. release builds

[`installers.yml`](../.github/workflows/installers.yml) validates that the installer **sources compile**
on every push that touches `installer/**` (and runs an opt-in end-to-end SQL-install smoke test). Those CI
builds are intentionally **unsigned**. Signing happens only at release time, in `release.yml`.
