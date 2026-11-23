# Product update: week of 23 November 2026

## A staging environment for the clean edition

The clean edition of the platform now has its own staging environment on Azure, separate from the live site. New work can be tried there first before it reaches clients. It runs on the existing Azure plan, so it adds no monthly cost.

All of its passwords and keys are read from Azure Key Vault, Azure's secure store, rather than being kept on the server itself.

## Security documentation for banks

A new Security and Trust Overview explains to a bank's IT and security team how the platform protects their information: encryption, sign-in and access control, separation between banks, testing results, and how the AI is governed and reviewed by people. It is ready to share with banks we are in conversation with. Alongside it, the team now keeps a full internal register of every security control and its status, which guides the next improvements.

## Being worked on

The staging server is waiting for one permission on the Key Vault before it can read its keys. Once that is granted, staging will be checked end to end: sign-in, documents and an analysis.
