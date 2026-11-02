# Security

Do not include a generated QX task URL, local credential, config/state file, subscription, browsing data, or unredacted screenshot in a public report.

Use the repository's **Security → Report a vulnerability** feature for a suspected vulnerability. If private reporting is unavailable, open an issue asking for a private contact route without including exploit details or private data.

The initial supported line is 0.1.x. The app runs as the current user, with no privileged helper. It has no cloud telemetry. Its loopback interface is not intended as a boundary against the same OS user or an administrator. See the README and architecture notes for stored data, limits, and trust assumptions.

Release archives are initially ad-hoc signed and not notarized. Checksums detect a damaged/mismatched download; they are not a replacement for Developer ID signing. Review the source and build locally if your environment requires a stronger distribution trust model.
