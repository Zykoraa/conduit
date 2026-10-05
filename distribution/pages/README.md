# Conduit downloads

This repository publishes Conduit release downloads and signed update feeds at
https://zykoraa.github.io/conduit/.

The application source, production signing key, device profiles and private
diagnostics are kept outside this distribution repository. The Windows app
verifies feeds against its embedded release public key. GitHub account access
alone cannot authorize a differently signed app update.

## Deployment

Publishing a release runs the pinned Pages workflow. The workflow downloads only
the two matching Setup files, two versioned update bundles, two signed feeds and
checksums. It verifies every file hash before deploying the static site. The
download artifacts are release assets, rather than large binaries in Git history.

Pages uses the workflow deployment mode. Its `github-pages` environment permits
the `main` branch and release tags matching `v*`; the workflow also validates the
tag's exact version format before fetching files.

Feeds expire thirty days after signing. The publisher must renew signed feed
metadata before expiry using the original protected key and redeploy the release.
The key never runs in GitHub Actions. A failed or expired feed cannot authorize an
update. Retired versions remain available in GitHub release history.
