# Product update (26 Oct 2026)

## Dev environment

We published a fresh API and web build to the dev Azure environment from release branch release/2026.10.26.01 (build label 2026.10.26.01 for both apps).

Use https://bcp-web-dev.azurewebsites.net as usual. After login, hard refresh your browser (Ctrl+Shift+R) and open Settings to confirm the build version matches on web and API.

# Product update (28 Oct 2026)

## Source code security clean-up

Our code hosting account had been accessed by an outside party, who pushed altered copies of recent work with harmful scripts hidden inside. We checked every copy of the code, on our own machines and in the hosted repository. The working copies used to build and deploy the product were clean, so no deployed build was affected.

All affected branches in the hosted repository have now been restored to the genuine code, and a full re-check found no remaining harmful files. Access credentials have been reset and two-factor sign-in is now in place.

## Being worked on

We are adding a routine check that runs before any code is pulled from the hosted repository, so that any future tampering is caught before it reaches a developer machine.

# Product update (29 Oct 2026)

## See where each action was embedded

A new View embedding button, next to Submit and Finalize and at the top of the report, opens a side panel on the right. Before the report is finalized, it shows which resolved action will be written into which internal document and on which page, so reviewers can check the placement first; the policy wording itself is written at finalize. After finalizing, the same panel lists every updated document with the clause, page, action and the text that was written in, and each document can be downloaded from there. The heading of each embedded note is now simply "POLICY UPDATE - Regulatory Clause" followed by the clause number, without a gap number.

## Upload several documents at once

Regulation documents and internal documents can now be uploaded several at a time. Choose multiple files in the upload window and they are added one after another, with a progress message and a summary at the end.

## Business settings and module switches

Business administrators have a new Business settings page under Admin settings, where two modules can be switched on or off for their business. Turning Actions off hides corrective actions in analysis reports, while gaps and everything already recorded stay exactly as they were. Turning Embeddings off means finalizing a report no longer creates updated versions of the internal documents. The platform administrator can set the same switches for any business from the Business workspaces page. Both modules are on by default.

## Upload limits

Each business can now have upload limits, all in MB: the largest single document, the largest combined size when several files are uploaded together, and the total size of its document library. The platform administrator sets them on the Business workspaces page, and the business sees them, along with how much storage it has used, on its Business settings page. When files are chosen for upload, they are checked against these limits first, and a clear message explains which file is too large or how much space is left. A business with no limits set works exactly as before.

## Guide for managing sales in Jumppl

We prepared a guide for running the sales of the platform in Jumppl. Each target bank becomes one item in the sales project, moving through clear pipeline stages from Lead to Won or Lost, with the bank's details such as bank type, emirate, decision maker and expected deal value kept in fixed fields. The client can keep the bank list up to date from the client portal, and an existing list in Excel can be imported in one step. The guide also describes the shared document folders, how the sales team asks the development team for demos and pilots, how a won bank is handed over for onboarding, and a checklist of what is set up at the start. The sales project now has these pipeline stages and is organised by country (UAE, Saudi Arabia and Pakistan) and then by bank type, with fields on every bank for its contacts, lead source, expected deal value and key dates, and the shared document folders for the sales team are in place. The client follows both the sales pipeline and the development work, with their document folders, through one client portal.

## Simpler progress while an analysis runs

While an analysis runs, everyone now sees a simple status at the top of the page, such as "Processing your analysis - 12 of 40 clauses checked". The detailed technical progress panel on the right is kept for the platform team, and it can now be switched off from Platform settings, in which case the simple status is shown instead.

## Faster and more accurate embedding preview

The embedding preview now opens much faster, and each document in it can be folded open or closed. Page references are more accurate: when an action names the section of the internal document it changes, such as "Amend 7.5 Customer Risk Assessment", the note is placed on that section's page, and regulation rule numbers mentioned in the same action are no longer mistaken for it. The preview itself does not use any AI.
