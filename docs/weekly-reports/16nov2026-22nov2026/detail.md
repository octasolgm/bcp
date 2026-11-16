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
