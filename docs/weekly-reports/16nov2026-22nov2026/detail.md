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

Importing a rulebook used to sit with the button spinning for a long time on the larger packs, because each category was saved to the database on its own. The whole import is now saved in one step, so it completes promptly.
