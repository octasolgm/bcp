# Product update: week of 23 November 2026

## A staging environment for the clean edition

The clean edition of the platform now has its own staging environment on Azure, separate from the live site. New work can be tried there first before it reaches clients. It runs on the existing Azure plan, so it adds no monthly cost.

All of its passwords and keys are read from Azure Key Vault, Azure's secure store, rather than being kept on the server itself.

## Security documentation for banks

A new Security and Trust Overview explains to a bank's IT and security team how the platform protects their information: encryption, sign-in and access control, separation between banks, testing results, and how the AI is governed and reviewed by people. It is ready to share with banks we are in conversation with. Alongside it, the team now keeps a full internal register of every security control and its status, which guides the next improvements.

Releases to staging now carry no passwords or keys at all. The server reads each one from Key Vault when it starts, so a key is changed in one place and never travels with the software. The release tools also refuse to send the new edition to the live site by mistake.

## Being worked on

Staging is checked end to end next: sign-in, documents and an analysis. The live site keeps its current key setup until the new edition replaces it.
