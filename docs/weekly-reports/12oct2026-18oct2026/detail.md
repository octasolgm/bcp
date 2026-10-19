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

Progress while re-checking gaps

When you re-check open gaps against newly uploaded evidence, the progress panel now shows clearly that work is happening: the current stage and each clause being checked have a spinning indicator, and a progress bar fills as clauses are completed.

Re-check results in the exported report

The Excel and PDF reports now include what each evidence re-check found. In Excel, a new "Evidence re-check" sheet shows, for every gap that was re-checked, whether the new evidence fulfilled it, what it covered, what is still missing, and the exact passages found in your documents with their page numbers. A second sheet, "Re-check actions", shows what happened to each corrective action: resolved, split into a done part and a remaining part, or unchanged. The PDF report lists the same results under each clause. Both sheets can be switched off in the export options.

The Policy Extract column of the exported report now reads the same as the analysis report page: each document reference is shown with the passage quoted from it, rather than a numbered list in a separate column. When gaps have been re-checked against newly uploaded evidence, the passages found in that evidence follow underneath, labelled "New evidence" with the document, section and page.

Document status on the New analysis page

When you open the New analysis page, each document now shows "Checking..." with a small spinner while the page finds out whether it has been parsed and extracted, instead of briefly showing a red "Not parsed / not extracted" and "0 pts". The real status and clause count appear as soon as the answer arrives. If the answer is slow, the page says so, and if it cannot be reached it shows why and keeps trying.

Being worked on

We saw the document check take over two minutes once. It normally answers in under a second, so we are looking into what slowed it down at that moment. We also noticed that starting an analysis begins immediately, without a confirmation step, and are checking whether one is wanted.

Importing documents from cloud storage

We have written up how workspaces will connect their own OneDrive, SharePoint, Google Drive, Dropbox, Box or pCloud account and import documents straight into Internal or Regulation documents. A workspace admin clicks Connect, signs in with their own account and allows read-only access, then picks files to import. Access can be removed at any time, and every import is recorded. Google Drive is now built: under Admin settings, a new Integrations page lets a workspace admin connect a Google account in a pop-up, and an Import from cloud button next to Upload on the document pages lets admins and makers browse folders, search, and import PDF, Word or Google Docs files, which then wait to be parsed and extracted like any upload. Access is read-only, every import is recorded, and the connection can be removed at any time. OneDrive, SharePoint, Dropbox, Box and pCloud are listed as coming soon.

In-house deployment for banks

We wrote a plan for installing Comply Solutions on a client's own servers or private cloud, and a plain-language brief explaining how documents are protected. It covers the options (hosted demo, in-house with approved cloud services, fully contained), exactly what data goes where, the controls in place, how a deployment is delivered in phases, and what the client and we are each responsible for. The hosted portal is described as a demonstration environment for sample documents.

Stronger data protection for AI and document services

Requests to AI models through OpenRouter now ask for zero data retention and no training on your data, so the model providers that answer are only those with that policy. We tested every model we offer: all the main models still work (Anthropic, OpenAI, Google, xAI, DeepSeek, Moonshot, Z.AI), while three Qwen models and the free-tier models cannot be served under these rules and now show a clear message instead. Installations can also switch off the optional full-text policy review, restrict which outside services the platform may contact, and require secure connections for all of them. An administrator can see which of these protections are in effect. We also checked that application logs never contain document text, prompts or AI answers.


How finalized actions appear in the updated internal documents

When a reviewer finalizes an analysis, each resolved action is turned into policy wording and added to a new version of the internal document, rather than repeating the action text typed during review. The wording is written by the AI model chosen in Admin settings, using the clause, the specific gap and the action as background, and is shown under a "Policy Update" heading that names the regulatory clause it addresses. In PDF files the text is placed on a new page right after the page where the evidence was found; in Word files it is placed as a highlighted paragraph next to the related text. The original version is never changed. A written guide to this process is now available to the team.

Clause limits for workspaces

Platform administrators can now limit how many clauses a client workspace may analyse in total, as well as, or instead of, a spend limit. The spend limit can be entered in dollars or in credits, with the equivalent shown alongside. When an analysis would take a workspace past either limit it does not start, and the message says how many clauses are left. The workspace settings now show spend limit, clause limit and margin side by side, each with its current usage.

Consistent policy text across documents

When an analysis is finalized and the same resolved action is written into more than one internal document, every document now receives the same policy wording. On the usage pages, these calls are now listed as "Finalize - policy text" with the clause and gap they were for, instead of "Other".

A cleaner Business workspaces page

The Business workspaces page now fits any window size. On narrower screens each workspace is shown as its own card instead of a wide table, and the create form no longer cuts off fields. Opening a workspace's credits now shows a tidy summary of credits, cost, billed amount, profit and clauses analysed, followed by the spend limit, clause limit and margin settings with usage bars, then adding credits, date format and spend by model.

Being worked on

On phone-sized screens the main menu stays open and takes up most of the screen. We plan to turn it into a menu that slides out when needed.
