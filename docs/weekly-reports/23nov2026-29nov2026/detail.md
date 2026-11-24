# Product update: week of 23 November 2026

## A staging environment for the clean edition

The clean edition of the platform now has its own staging environment on Azure, separate from the live site. New work can be tried there first before it reaches clients. It runs on the existing Azure plan, so it adds no monthly cost.

All of its passwords and keys are read from Azure Key Vault, Azure's secure store, rather than being kept on the server itself.

## Security documentation for banks

A new Security and Trust Overview explains to a bank's IT and security team how the platform protects their information: encryption, sign-in and access control, separation between banks, testing results, and how the AI is governed and reviewed by people. It is ready to share with banks we are in conversation with. Alongside it, the team now keeps a full internal register of every security control and its status, which guides the next improvements.

Releases to staging now carry no passwords or keys at all. The server reads each one from Key Vault when it starts, so a key is changed in one place and never travels with the software. The release tools also refuse to send the new edition to the live site by mistake.

## Stronger sign-in and a full audit trail

The new edition, now on staging, adds the sign-in protections banks ask about first.

- Two-step sign-in: after their password, people enter a 6-digit code from an authenticator app on their phone, such as Google Authenticator or Microsoft Authenticator. Set-up takes a minute with a QR code, or by typing a key. Administrators can reset it for someone who loses their phone, and a business can require it for everyone.
- Sign-in PIN: people who prefer not to use an app can choose a 6-digit PIN instead. It is checked after the password and locks after five wrong tries.
- Stronger passwords: new passwords need at least 12 characters and a mix of character types, and common or easily guessed passwords are refused.
- Audit log: administrators can now see who signed in, failed sign-in attempts, security changes, every change made and what was opened, with filters and a spreadsheet export. Entries are kept for three months.

## Up-to-date foundations and safer AI

Both editions of the platform have moved to the latest version of their web framework and PDF export library, which clears every known vulnerability in the web app's libraries. Before any text is sent to an AI service, personal details such as email addresses, phone numbers, bank account numbers, card numbers and ID numbers are now replaced with placeholders, so they never leave our servers. The AI is also told, on every request, to treat document content strictly as material to assess and never as instructions. In the new edition, people using two-step sign-in now get ten one-time backup codes for the day they lose their phone.

## Being worked on

A separate staging copy of the sign-in service and database, so testing never touches live accounts, and a full check of the updated live edition before it is released.
