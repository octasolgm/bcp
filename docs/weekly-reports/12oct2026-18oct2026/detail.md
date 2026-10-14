Weekly Update - 12 Oct 2026 to 18 Oct 2026

How the compliance analysis works

We wrote a plain description of the whole compliance analysis, from uploading a document to the final gap report. It explains how documents are read and divided into sections and search passages, how each regulation clause is searched for in your policies, how the review decides what is covered and what is missing, how every gap gets a second check, and how results are reviewed, exported and finalized. It also lists what uses an AI model and what does not, and the limits to keep in mind.

Product demo

A demo of the analysis is planned for this week. It walks through one money laundering clause from the policy documents to the gap report and the Excel and PDF exports, using results from your own documents.

Sound when an analysis finishes

The analysis page can now play a short chime when an analysis finishes, so you do not need to watch the screen. Administrators can also choose a spoken summary after the chime, which reads out how many clauses are compliant or partially compliant and how many gaps were found at each risk level. The sound is produced by your browser and uses no AI credits. It is off by default and can be turned on in the analysis settings.

New build on the dev environment

We published a fresh production build of the API and web app to the dev Azure environment. The API package includes current configuration for database and integrations, and the site was restarted and checked for readiness before handoff. The web app was built for production with the live API URL baked in and deployed as a static bundle. You can use https://bcp-web-dev.azurewebsites.net against https://bcp-api-dev.azurewebsites.net as usual.

Choosing clauses for a new analysis

When a regulation is selected on the new analysis page, the page now shows the regulation's full clause list straight away, instead of a smaller number that changed a moment later. No clauses are pre-selected any more, so an analysis only covers the clauses you choose, and the progress counter shows exactly that number.

AI usage for each workspace

Workspace administrators now have their own AI usage page under Billing. It shows how many credits the workspace used and what that was billed, the remaining balance, a breakdown by activity (analysis, reruns, gap re-checks) and by AI model, and a list of every AI call, top-up and adjustment. It can be filtered by period, model, activity and entry type. Each workspace only ever sees its own usage, and the page shows only what the workspace was billed.

Document parsing with Azure Document Intelligence now appears in AI usage as its own activity, "Document parsing", with the service name and how many pages of which file were parsed, instead of an unlabelled "Other" line.

Dev environment refresh (14 Oct)

We published another production build of the API and web app to the dev Azure environment, with the same health checks as before. Use https://bcp-web-dev.azurewebsites.net as usual.

Being worked on

A clause analysed in a newly created workspace came back compliant, while the same clause found three gaps in the main workspace. We found the reason: that workspace's administrator account has "demo" in its email address, so the platform treats it as a demonstration account and shows the fixed demonstration result instead of running a real analysis. Creating the workspace administrator with an email address and name that do not contain "demo" gives a normal, live analysis. We are also checking whether two display issues seen in that workspace come from the same cause.
