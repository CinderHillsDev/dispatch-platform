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
4. Builds no macOS artifact. #48 shipped a universal tarball and a `.pkg`; both were withdrawn after the
   daemon turned out to be killed at exec on real Apple Silicon hardware - the payload inherits the
   download's `com.apple.provenance` attribute and Gatekeeper refuses it, a failure no CI runner can
   reproduce because runners build locally and never download. The macOS pipeline lives on the
   `macos/support` branch until there is a Developer ID to sign and notarize with. See
   [`SPEC.md` §16.7](SPEC.md).
5. Builds the **upgrade package** (`dispatch-upgrade-<ver>.tar.gz`) - one cross-platform file with every
   platform's payload (linux-x64, linux-arm64, osx-arm64, osx-x64, win-x64) and a signed manifest - for the
   dashboard's "upload an upgrade package" self-update flow (see [Upgrading](https://docs.dispatchrelay.app/deployment/upgrading/)).
   It is signed if `DISPATCH_UPDATE_SIGNING_KEY` is set (see below).
6. Generates `SHA256SUMS` and publishes a **GitHub Release** with all assets and auto-generated notes.

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
| Windows  | **`DispatchSetup-<ver>-x64.exe`** | The single file. Installs the self-contained service, which defaults to bundled SQLite - no database server required. To use an existing PostgreSQL/MariaDB/SQL Server, pass its connection string at install time (see [database.md](database.md)). The MSI is embedded in this bundle, not published separately. |
| Linux    | `dispatch-<ver>-linux.tar.gz` | Universal (x64 + arm64). Extract, then `sudo ./install.sh --admin-password ...` (auto-detects arch). |
| Upgrade  | `dispatch-upgrade-<ver>.tar.gz` | One cross-platform package for the **dashboard self-update** (Updates page), carrying `linux-x64`, `linux-arm64` and `win-x64` payloads. Upload it on any appliance/Linux/Windows install. |
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

Azure Artifact Signing - renamed from Trusted Signing in January 2026, same service, ~$9.99/month - signs
Windows binaries through an OIDC-trusted identity, so **no certificate, PFX, or password is ever stored in
GitHub**. The runner exchanges its GitHub-issued OIDC token for an Entra token and Azure performs the
signature; the private key does not leave Azure. That is the only authentication mode this pipeline supports
and it is deliberate: a long-lived signing secret in Actions secrets is the thing that gets leaked.

Until the variables below exist, every signing step is skipped. What changed is what happens on a version
**tag**: the job now fails outright rather than publishing an unsigned installer, because for months
`docs/RELEASING.md` asserted "the bundle and MSI are signed with Authenticode" while nothing checked, and a
PE parse of the released `DispatchSetup.exe` showed an empty certificate table - no signature at all, ever,
because the steps key off variables nobody had set. Rehearsal builds (`workflow_dispatch`) still skip signing
and say so in a warning.

What gets signed, in order, on every release:

| Artifact | When | Why this order |
|---|---|---|
| `publish/*.exe` (the service, minus `createdump.exe`) | before the MSI is built | an MSI signature proves who packed it, not who wrote what it installs. Without this the binary that runs as SYSTEM lands on disk unsigned |
| `Dispatch-<ver>-x64.msi` | after `wix build` | the package identity |
| the Burn engine | detached, signed, reattached | WiX-recommended; an unsigned inner engine makes UAC flag the bundle |
| `DispatchSetup-<ver>-x64.exe` | last | the file users actually run |

`createdump.exe` is excluded on purpose - it is a Microsoft-signed crash-dump helper from the runtime pack,
and replacing its signature with ours buys nothing.

A **Verify the signatures** step then runs `Get-AuthenticodeSignature` over all of the above and fails the
release if anything is not `Valid`, printing each file's status plus signer subject and SHA-1 thumbprint into
the job summary. "We asked for a signature" and "the artifact carries one that validates" are different
claims; only the second one is worth publishing.

### Setting it up

1. Create the **Artifact Signing account** and a **certificate profile** (the account name you type into
   GitHub is the Azure *resource* name - lowercase with hyphens, from the account's Overview page - not the
   display name). Note the account name, profile name, and the regional endpoint shown there, e.g.
   `https://<region>.codesigning.azure.net/`.
2. Grant the signing identity the **Artifact Signing Certificate Profile Signer** role on the account.
3. Create an **Entra app registration** with no secret, and add a **federated credential** for GitHub. Scope
   the conditions - this is the whole security value of OIDC, and a `*` here turns your signing certificate
   into something any branch can use:

   ```
   repo:CinderHillsDev/dispatch-platform:ref:refs/tags/*      # tagged releases
   repo:CinderHillsDev/dispatch-platform:ref:refs/heads/main  # dry runs from main
   ```

   Do not add `...:ref:refs/heads/*` or `...:*`: `release.yml` is `workflow_dispatch`-able from any branch,
   so a permissive condition lets anyone with push access mint a valid Dispatch signature over code that was
   never reviewed on main.
4. In repo Settings → Secrets and variables → Actions, add them as **variables** (not secrets - the `if:`
   gates read `vars.` and a secret there silently never matches):
   - Variables: `AZURE_SIGNING_ENDPOINT`, `AZURE_SIGNING_ACCOUNT`, `AZURE_CERT_PROFILE`
   - Secrets: `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`
5. Rehearse with a `workflow_dispatch` run on `main` - it takes the tag-free path but signs, because signing
   is gated on configuration, not on ref. Confirm the verify step reports `Valid` for all four kinds of
   artifact, then push a real tag.

CI on ordinary pushes and PRs stays **unsigned** by design: `installers.yml` builds installers to test them,
and a signature on an unreviewed build would be a signature on nothing.

> SignPath Foundation: as an Apache-2.0 project Dispatch may qualify for their free OSS program, which is
> also OIDC-based (it has no long-lived key either). It is not a reason to skip Artifact Signing if you have
> already paid for it - the pipeline is written for Azure, and swapping later means changing four steps.

### macOS: not signed, not shipped

A probe against the hosted runners settled what CI can do there: `security find-identity -v -p codesigning`
reports **0 valid identities**, so nothing can be signed with a Developer ID, and `notarytool` - which is
present - has nothing to submit. Ad-hoc signing was tried in #48 and is not sufficient: on Apple Silicon a
binary that arrives through a browser carries `com.apple.provenance`, cannot have it removed even as root, and
is killed at exec if it is not notarized. The failure is silent at the level of the installer (launchd reports
a job that keeps trying to start) and invisible on a runner, which is exactly the combination that shipped a
broken platform once already.

So there is no macOS artifact in a release. When a Developer ID and its notary credentials become available,
`macos/support` has the whole pipeline waiting - install script, launchd units, update applier, `.app` and
`.pkg` builders, and the macOS CI smokes - and the signing step slots in wherever `codesign` is called today,
followed by `notarytool submit` + `stapler staple`.

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

[`installers.yml`](../.github/workflows/installers.yml) validates the installers on every push that touches
`installer/**`: it compiles the Windows installer sources and lints the Linux scripts, and - on
`installer/**` pushes and manual dispatch - runs the real end-to-end Windows and Linux install smokes on the
target OS. The macOS smokes that used to live here went with the platform to `macos/support`, where the
lesson is recorded: an installer that CI can install perfectly on a machine that cannot fail the way a user's
machine fails is not evidence. Those CI builds are intentionally **unsigned**. Signing happens only at release
time, in `release.yml`.
