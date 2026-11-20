DO NOT INSTALL ENGINES MANUALLY.

The publish.ps1 script calls scripts/bundle-engines.ps1 before building.
All Windows executables and SVGO's portable Node.js runtime are collected here
and copied into the final published ZIP. This source ZIP intentionally contains
no unverified third-party executables; they are downloaded and SHA256-verified
by the build script on a Windows build machine.

See ../../../THIRD_PARTY.md for exact versions, sources, attribution and
redistribution guidance.
