# Product update - week of 9 to 15 November 2026

## Security review

We carried out a security review of the platform this week. The review looked only at the application and did not change anything in it. The platform held up well on the core protections that matter most for client data: the database is accessed safely so it cannot be tricked by malicious input, sign-in tokens are properly verified, each client bank's data stays walled off from every other, and uploaded files are handled safely. A full report was prepared that lists every observation by importance, with clear explanations and recommended fixes.

The review also noted a set of hardening improvements to schedule, none of them an open door to client data on their own. The main ones are adding request rate limits to slow down automated abuse, making the sign-in requirement apply automatically everywhere by default, adding standard protective web headers, narrowing which websites may call the service, and returning tidy error messages instead of technical ones.

We also ran the platform on a local machine and tested it as a live, running system in addition to reading the code. The live test reconfirmed that sign-in is properly enforced on every protected area, and it confirmed the hardening points above against the running app. We then went a step further and tested while signed in as an administrator: attempts to forge or tamper with a login token were correctly rejected, and requests for records outside the account's scope returned a clean "not found" with no data leak. We then ran that signed-in test using two ordinary (non-administrator) accounts in two separate workspaces. A normal user in one workspace could not read, list, or move into another workspace's data in any way we tried, and a user could not even open work created by a colleague in their own workspace. The temporary test accounts were removed afterwards. We also reviewed how data is protected in transit: the application itself does not currently force encrypted connections and depends on the hosting platform for that, so we recommend confirming the live site's encryption settings and having the application enforce secure connections as well. For formal regulatory sign-off, an assessment by an accredited external security firm is still recommended.

## Security improvements applied

Following the review, we acted on the findings the same week. Most importantly, while checking the database access rules we found and immediately fixed a serious issue: a protective setting had not been switched on for a number of data tables, which could have allowed certain records to be read without signing in. We corrected this straight away, confirmed that the previously reachable data is now fully blocked, and verified that the platform continues to work normally for signed-in users. This was the single most important item and it is now resolved.

We also put in place a set of protective improvements across the service: limits that slow down automated or abusive bursts of requests, automatic enforcement of sign-in on every protected area, stricter control over which websites are allowed to connect, standard protective web settings in the browser, tidier error messages that no longer reveal internal detail, and safer handling of uploaded files. Each change was made carefully and checked against the running system so there is no disruption to normal use. A small number of minor tidy-up items remain, along with a recommended check of the live site's encryption settings and, for formal sign-off, an assessment by an accredited external security firm.

## Regions and regulatory libraries

Each business can now record the country it operates in, chosen from a list grouped by region. The first region covered is the Middle East, with the United Arab Emirates, Saudi Arabia, Qatar, Oman and Bahrain. The choice is made once, in Business settings, and it does two things automatically: it sets the date format the business sees, and it decides which regulators' rulebooks that business can bring into its library.

On the rulebook page there is now a single Import rulebook button. It brings in the rulebooks for that business's own country, organised by region, then country, then regulator. It is safe to use more than once: anything already present is left alone and only what is missing is added, so a business that has already built up part of its library will not get duplicates. The regulators covered so far are Abu Dhabi Global Market for the UAE, the Saudi Central Bank, the Qatar Financial Centre Regulatory Authority, the Central Bank of Oman and the Central Bank of Bahrain.

For administrators, the list of businesses now shows each one's region and country at a glance, and the column headings stay in place while the list is scrolled.

The links supplied for these regulators are their rulebook websites rather than single documents, so they are stored as reference links for now; pulling in a specific rulebook document is done per rulebook once its document link is known. Further countries will be added as their links come through.

## Being worked on

We have started building a cleaned up edition of the platform in a new code home. The web app part is done: it now opens directly at the main address, keeps only the current dashboard and its finished features (the New analysis workflow, documents read with Azure Document Intelligence, review and approval queues, and administration), and no longer contains demo accounts, older test pages or earlier analysis versions. It keeps using the existing data, so nothing needs to be migrated.

Next we will clean up the server side the same way and switch all AI analysis to a single Azure hosted model (Kimi K3), with an admin setting ready for more models later. Moving the database, file storage and sign-in to Azure will follow once the cleaned edition has been fully tested.
