Weekly Update - 19 Oct 2026 to 25 Oct 2026

Using the platform on a phone

The platform now works on a phone screen. Before, the left navigation menu always stayed open, which on a phone left only a thin strip for the page itself. On narrow screens the menu is now hidden by default and every page uses the full width of the screen. A menu button at the top left opens the navigation as a panel that slides in over the page; it closes again when you pick a page, tap outside it, or press Escape. The top bar was also tightened so the logo, New analysis, the workspace switcher, your role and the notifications bell all fit on a small phone without scrolling sideways.

On tablets and computers nothing changes: the navigation stays where it was, it can still be collapsed and widened by dragging its edge, and it remembers those choices. The menu items each account sees are the same as before.

The two side panels that appear while running an analysis, the progress tracker on the left and the administrator's pipeline panel on the right, now also suit a phone. Instead of sitting beside the page at full width, each shows as a slim tab at the edge of the screen. Tapping the tab opens the panel over the page, and tapping outside it or pressing Escape puts it away again. On tablets and computers both panels behave exactly as before, including collapsing and resizing.

Being worked on

On a tablet in portrait, when the navigation and both analysis side panels are open at once, they leave no room for the page itself until one of them is collapsed. On a small phone, a few fields on the New analysis page are also slightly cut off at the edge. Both are being looked at next.

We are also designing an in-house option for banks that need the platform to run inside their own network, so their documents never leave it. Each installation would run under a license that we issue, covering the subscription period and how much analysis it includes. Renewal can happen automatically through a small daily check-in that sends only usage totals, never documents, or by license file for fully offline sites. If a subscription lapses, past results stay available to view and export, and new analysis pauses until it is renewed.

For these in-house installations, banks will be able to choose how the AI part is provided. We can supply it for them, with a separate, capped AI account for each bank and usage charged as credits, or the bank can connect its own AI accounts in its own cloud tenant and pay the provider directly. In both cases the platform itself is a monthly subscription with an included amount of analysis.

In-house installations will also receive product updates as tested, signed release packages. The bank's IT team installs each update in its own change window, first on a test system and then in production, and can always return to the previous version if needed.

The in-house offering is planned around three things a bank can buy: a free trial on its own documents, a monthly or yearly subscription plan, and add-ons such as extra users, an extra workspace or one-off packs of additional analysis. A trial converts to a paid plan without reinstalling and keeps all the work done during the trial.

All of this now sits in one in-house deployment plan covering every way the platform can be installed: on a single server for a pilot, across several servers, in the bank's own private cloud, or in a fully closed network with no outside connections. A typical bank should be able to go from the first technical meeting to live use in about six to nine weeks, most of which is the bank's own approvals and server setup.

For banks that use Microsoft Azure, the plan now also describes a packaged installation: one script that sets up the whole platform inside the bank's own Azure account, in the region it chooses, with no public access and all keys kept in the bank's own key vault. The same script previews every change for the bank's approval first and later installs updates.

Dev environment deploy

We published fresh API and web builds to the dev Azure environment (API build 2026.10.19.04, web 2026.10.19.05). Use https://bcp-web-dev.azurewebsites.net as usual. After login, hard refresh your browser (Ctrl+Shift+R) and open Settings to confirm the build version.

The latest dev deploy includes the phone layout work above (API 2026.10.19.06, web 2026.10.19.07). Hard refresh again if Settings still shows an older web build.

Security testing

An independent security testing team has started work on the platform. We prepared a system guide for them. It starts with a short version of the main flow and then covers every feature: setting up client workspaces and their limits, creating users with maker, checker and reviewer roles, uploading and preparing documents, running a compliance analysis, working on gaps, actions and reviews, the approval flow up to final sign-off, and the corrected policy document produced at the end. It lets them test the full product on the dev environment without affecting existing data.

Before the external testers go deeper, we also ran our own full security review of the platform, covering sign-in and permissions, separation between client workspaces, document uploads, stored keys, the web hosting setup and third-party components. We now have a prioritised list of improvements, and the most important ones are being fixed first, starting with stronger access checks and replacing credentials, before the external testing continues.

Installing in a client's own Azure

We prepared a ready-to-send installation package for clients who want the platform running in their own Azure account. The client receives one zip file with a step-by-step PDF guide. Their IT team fills in a short settings file with their own keys, runs one installer, and the platform is set up and started in their Azure account, connected to their own database project. The package itself contains no keys and is identical for every client, so each client's details stay only in their own environment.

Updates arrive as a new numbered package. Installing one is a single command that takes a few minutes and keeps all settings, users, documents and history; going back to the previous version is the same command run from the older package. Every file in the package is checked before anything is installed, and clients can confirm the download with a fingerprint sent to them separately.

The client installation package now contains only the parts of the platform that clients use today: the New analysis page and the Documents pages, where uploaded documents are read by Azure Document Intelligence. Older analysis screens and internal test pages for other document-reading engines are hidden in client installations. Clients now need only three services of their own: an AI analysis key, Azure Document Intelligence and Azure OpenAI for document search, all set up in their own Azure account. The guide explains how to create each one.

Client installations also start with exactly the same analysis setup as our own service, including the latest analysis instructions and AI model choice, so results match what clients have seen in demonstrations. Every update brings the newest analysis improvements, while any changes a client's administrator has made are kept.

We also fixed an issue on the New analysis page where, with a larger number of documents in the library, the document list could stay on "Checking document status" with a message that the service could not be reached. The page now checks documents in smaller groups, so it works however many documents are listed. This fix is live on the dev environment (web build 2026.10.23.01); hard refresh the page (Ctrl+Shift+R) to pick it up.

Two more reliability fixes are packaged in release build 2026.10.23.01 (API and web). Every page request now confirms your sign-in directly on our server instead of asking the sign-in service each time, which makes pages respond faster and removes occasional failures when that service was slow. The Regulation Documents list could also sometimes appear without its documents when the page loaded; it now loads reliably every time. After we push that build to dev, hard refresh and check Settings for matching API and web version 2026.10.23.01.

Simpler document preparation and clearer document pages

Uploading a regulation or internal document from the New analysis page now prepares it fully in the background, the same way evidence uploads on an analysis report already work. The page shows each step as it happens, and once the document is ready it is selected and its points are shown. Preparation carries on even if you leave the page. The document lists on New analysis now show only documents that are ready to analyse, together with the date each one was uploaded.

On the Internal Documents and Regulation Documents pages, the status column is now easier to read. A parsed document shows a green tick with an "Extract now" link underneath, a document that has not been parsed yet shows a "Parse now" link, and a document that is ready to analyse is shown in green with a tick. Each row now has a single "View" button, and technical options that were only meant for our own testing have been removed from these pages. The points panel shows a simple point count without internal statistics.

In an analysis report, choosing a point now moves it to the top of the points list on the left, so the following points are easy to reach.

The overview cards now open exactly the records they count. Before, a card such as "Medium risk gaps" could show one number on the overview and a smaller number, or nothing, once opened. Opening a card now lists every analysis that contributes to the number, adding up to the same total.

