# Product update - week of 16 to 22 November 2026

## Managing businesses is quicker to read and to use

The list of business workspaces has been reworked so it shows more at a glance and takes fewer clicks.

Each business's region can now be set directly in the list. Where it previously only displayed the region, the value itself is now clickable: pick the country from a short list grouped by region and it saves straight away, updating the date format that follows from it at the same time. There is no need to open anything first.

The list itself is also much denser. Each business previously took three lines of height because its buttons were laid out two to a row, which left a lot of empty space and meant only two or three businesses fitted on screen. The actions are now compact and sit on a single short row. Users, Credits and Settings used to be three separate buttons that each opened their own panel; they are now tabs inside one Settings panel, so each business shows just Open, Settings and Deactivate. When a panel is open it names the business and repeats its region, user, document and analysis counts and status, so the context is clear even after scrolling.

Renaming a business has moved onto the name itself: a small pencil sits beside it, and the change is confirmed with a tick. The column headings now stay in place while the list is scrolled, and a "Back to top" shortcut appears once you have scrolled down.

## Clearer guidance when importing rulebooks

Which rulebooks a business can import depends on the country it operates in. Previously the Import rulebook button could be pressed before a region had been chosen, and would then simply report an error. It is now unavailable until a region is set, and says why, with a link to the settings page where the region is chosen.

## Now on the test environment

Everything described above has been released to the test environment, along with the security improvements made earlier in the week. We checked the live site afterwards and confirmed the protective measures are working there: secure-connection enforcement and the standard browser protections are in place, requests without a valid sign-in are refused, and the overly broad website trust that the security review flagged is now closed.

Managing rulebook categories also became easier: several can be selected at once and removed together. Because removing a category also removes everything filed under it, this asks for confirmation first and is explicit about what will go.

## Being worked on

The release is on the test environment but has had little hands-on use so far, so the next step is working through it there before it goes any further.

The create-business form at the top has since been folded away behind a button, so the page now opens on the list. The look of the list is still not settled and is the main thing to pick up next. More regulator links are also expected, and those supplied so far point at rulebook websites rather than individual documents, so each specific rulebook document is still added separately.

Importing a rulebook used to sit with the button spinning for a long time on the larger packs, and on the largest ones gave up before it finished. Each category was being written, and then checked for duplicates, one at a time. The whole import is now prepared in one pass and written once. A full UAE import, 256 categories covering 205 rulebooks, now completes in under two seconds.

The sidebar was also quietly asking the server about running analyses on every page, using a channel that is only open to the platform's own workspace. Inside a client business the request was refused every time and never had anything to report, so it now runs only where it applies. Nothing about this was visible on screen, but it removes a repeated failed request from every page.

This work is live on the test environment as build 2026.11.16.26.

## Rulebooks for the rest of the region

Until now only the UAE had a real regulatory library behind it. The other countries we support had a single entry each, a link to the regulator's website, so a business in Bahrain that pressed Import rulebook received one line rather than a library.

Each regulator's published rulebook has now been read in full and built into the library, so importing gives a working structure from the first day:

- Bahrain: the Central Bank of Bahrain rulebook, all seven volumes plus the common volume, each with its sections and modules, including the financial crime module
- Saudi Arabia: the Saudi Central Bank rulebook, every sector with its topics and the instructions issued under each
- Qatar: the QFC law and regulations together with all twenty QFC Regulatory Authority rulebooks, each broken down into its parts, the anti money laundering rules among them
- Oman: the Central Bank of Oman publishes individual laws, frameworks and guidance rather than a single rulebook, so each one is listed with a direct link to its document, which means those documents can also be pulled in automatically

Deliberately left out are quarterly updates, ad hoc communications, archived material, forms, consultation papers and circulars. These are published continuously and are a running history rather than part of the rulebook itself.

## A clean edition of the platform

The cleaned up edition of the platform has been brought up to date with everything delivered this week and last: the security improvements, regions per business, the regional rulebook libraries and the business workspace changes. It keeps the same features and works the same way, without the older test pages, demo accounts and unused engines, and it now opens directly at the main address. It uses the existing data, so nothing needs to be moved. The next step is a full walk through of the signed in pages, and connecting the Azure hosted AI model that analyses will run on.

The clean edition was then taken a step further, so it now holds only what the platform actually uses. Every screen, server function, setting and script was checked against what the pages call, and anything nothing calls was taken out, along with the settings for AI providers that are no longer offered. The setup and deployment guides were rewritten to match. Every page was opened again afterwards and works as before. While doing this we also found and fixed a step that would have stopped the server from being published from the new edition, and removed a sign-in message that pointed to a tool which no longer exists.

The clean edition is now connected to the Azure hosted AI model, Kimi K3, which replaces every other AI provider. It answers test requests in a few seconds, and the admin settings page shows it as the model for analysis. When more models are deployed on Azure, adding their names makes them selectable there.

The first full analysis on this model was slower than expected. Part of that came from the model thinking at its most thorough setting, which doubled the work for no gain; it now uses the same high setting the platform has always used, with no loss of accuracy. The rest comes from the speed of the Azure deployment itself, which is being reviewed with our infrastructure team. AI usage for these calls is now shown under the Azure AI Foundry name.

The keys the platform uses to reach its database and AI services now live in Azure Key Vault, Azure's secure store for secrets, instead of in settings files on developers' machines. This is the first piece of the move to Azure and changes nothing for users.

The clean edition no longer contains any connection to Landing AI, the older document reading service. Documents are now read only by Azure Document Intelligence, and anything read in the past is still used, so earlier work is not lost. Where a document has never been read, the platform now says so plainly and points to the Azure Document Intelligence step, instead of showing a technical error. Labels that still mentioned the old service have been reworded.

## Being worked on

A full move of the platform onto Azure. Three things are still hosted outside Azure today: the database, sign-in, and the store that holds uploaded documents. The work to come is a managed Azure database of the same kind we use now, so the data and the semantic search behind analysis move across unchanged, an Azure storage container for documents, a replacement for sign-in, and an Azure mail sender for password reset and invitation emails. Secrets that currently live in files on the server move into Azure's own secret store. The document reading service and the model that produces the search index are already running on Azure. Sign-in will be handled by the platform itself, with the same email and password every user has today, so nobody needs to reset a password, and it also suits a bank that hosts the platform on its own servers. The Azure resources have been requested at a size that fits current use, keeping the monthly cost low, with room to scale up later. Nothing changes on screen, and no data is lost in the move.

## Sign-in now runs through the platform's own service

Until now the sign-in page talked straight to the hosted identity service, which meant the browser's network activity showed a third party address and the project's public key sat inside the web app. That is no longer the case. Signing in, renewing a session, signing out, changing a password and opening a password reset or invitation link all go to the platform's own service, which then speaks to the identity provider privately on the server. 

For anyone using the product nothing changes: the same email and password, the same reset emails, the same session. What changes is what the product shows about itself. Everything on screen is served by the platform, the identity provider's address and keys have been removed from the web app entirely, and anyone inspecting the application only sees calls to our own service.
