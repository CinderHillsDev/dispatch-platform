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
3. Builds the **universal Linux tarball** (`dispatch-<ver>-linux.tar.gz`, x64 + arm64, self-contained) -
   `install.sh` auto-detects the arch; no .NET SDK needed on the box.
4. Builds the **upgrade package** (`dispatch-upgrade-<ver>.tar.gz`) - one cross-platform file with every
   platform's payload (linux-x64, linux-arm64, win-x64) and a signed manifest - for the dashboard's
   "upload an upgrade package" self-update flow (see [Upgrading](https://chrismuench.github.io/Dispatch-SMTP-Relay/deployment/upgrading/)).
   It is signed if `DISPATCH_UPDATE_SIGNING_KEY` is set (see below).
5. Generates `SHA256SUMS` and publishes a **GitHub Release** with all assets and auto-generated notes.

### Versioning

- Use `vMAJOR.MINOR.PATCH` (e.g. `v1.2.3`).
- A version containing a hyphen (e.g. `v1.2.3-rc1`) is published as a **pre-release**.
- The MSI uses `MajorUpgrade`, so bumping the version lets an installed instance upgrade in place.

### Dry run (no tag)

Run the **Release** workflow manually (`workflow_dispatch`) with a `version` input. It builds everything
and publishes a **draft** release you can inspect and delete - nothing goes public.

## What users download

| Platform | Asset | Notes |
|----------|-------|-------|
| Windows  | **`DispatchSetup-<ver>-x64.exe`** | The single file. Installs PostgreSQL → the MSI → the service. |
| Windows  | `Dispatch-<ver>-x64.msi` | Advanced: install against an existing PostgreSQL (`msiexec /i Dispatch-<ver>-x64.msi SQLCONN="Host=localhost;Port=5432;Database=DispatchLog;Username=dispatch;Password=..."`). |
| Linux    | `dispatch-<ver>-linux.tar.gz` | Universal (x64 + arm64). Extract, then `sudo ./install.sh --install-postgres ...` (auto-detects arch). |
| Upgrade  | `dispatch-upgrade-<ver>.tar.gz` | One cross-platform package for the **dashboard self-update** (Updates page). Upload it on any appliance/Linux/Windows install. |
| Any      | `ghcr.io/cinderhillsdev/dispatch-platform:<ver>` | Multi-arch (amd64+arm64) container image; pushed to GHCR by the `docker` job. |
| All      | `SHA256SUMS` | Verify downloads: `sha256sum -c SHA256SUMS`. |

## First release - one-time setup

The first tag push creates the GHCR package, but **GHCR packages are private by default**, so anonymous
`docker pull` will fail until you make it public:

1. Push the first tag (e.g. `v1.0.0`) and let the **Release** workflow's `docker` job publish the image.
2. Go to the repo's **Packages** → `dispatch-smtp-relay` → **Package settings** → **Change visibility → Public**
   (and, optionally, **Connect repository** so the package shows on the repo page).
3. Verify anonymous pull works on a clean machine:
   ```bash
   docker pull ghcr.io/cinderhillsdev/dispatch-platform:1.0.0
   ```

Subsequent releases reuse the same (now public) package - this step is only needed once.

## Code signing (Azure Artifact Signing)

Signing is **opt-in and dormant** until provisioned - until then releases publish unsigned (Windows
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

> Note: code signing currently uses Azure Artifact Signing (a paid service tied to your own identity).
> As an Apache-2.0 open-source project, Dispatch may also qualify for a free OSS code-signing program
> such as the **SignPath Foundation**.

## Upgrade-package signing key (one-time)

The dashboard self-update will only apply a package whose manifest is signed by the release key. The
**public** key is committed (`src/Dispatch.Core/Updates/dispatch-update-public.pem`, embedded in the app
and shipped to installs); the **private** key lives only in the `DISPATCH_UPDATE_SIGNING_KEY` GitHub
Actions secret. Until the secret is set, releases emit an **unsigned** package that the updater refuses.

Generate the key (RSA-3072) and store the public half in the repo, the private half in the secret:

```bash
# from the repo root
openssl genrsa -out dispatch-update-signing.key 3072
openssl rsa -in dispatch-update-signing.key -pubout -out src/Dispatch.Core/Updates/dispatch-update-public.pem

# store the PRIVATE key as a repo secret (never commit it)
gh secret set DISPATCH_UPDATE_SIGNING_KEY < dispatch-update-signing.key

# commit the regenerated PUBLIC key
git add src/Dispatch.Core/Updates/dispatch-update-public.pem
git commit -m "chore: set update signing key"
```

Then move `dispatch-update-signing.key` somewhere safe (a password manager / secrets vault) and delete the
local copy. **Keep it** - you need the same key to sign every future release, or already-deployed installs
(which trust the committed public key) will reject new packages. If you ever rotate it, re-run the steps
above and re-sign the interop test vector: `printf '{"version":"0.1.0"}' | openssl dgst -sha256 -sign dispatch-update-signing.key -out tests/Dispatch.Core.Tests/testdata/sample.manifest.json.sig`.

In the GitHub UI the secret is at **Settings → Secrets and variables → Actions → New repository secret**,
name `DISPATCH_UPDATE_SIGNING_KEY`, value = the full PEM (`-----BEGIN PRIVATE KEY-----` ... `-----END...`).

## CI vs. release builds

[`installers.yml`](../.github/workflows/installers.yml) validates that the installer **sources compile**
on every push that touches `installer/**` (and runs an opt-in end-to-end PostgreSQL-install smoke test). Those CI
builds are intentionally **unsigned**. Signing happens only at release time, in `release.yml`.
