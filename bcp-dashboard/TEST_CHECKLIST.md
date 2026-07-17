# End-to-End Test Checklist

Manual verification flows for the BCP Dashboard enterprise platform. Check each item after testing in a running environment (`bcp-api`, `bcp-dashboard`, Supabase, and optionally `bcp-web` for `/old`).

## Super Admin

- [ ] Login with super_admin credentials
- [ ] See admin navigation items
- [ ] Create a department
- [ ] Invite a maker user (email received)
- [ ] Invite a checker user
- [ ] Invite a reviewer user
- [ ] Deactivate a user — they cannot login
- [ ] Reactivate a user — they can login again

## Maker

- [ ] Login with maker credentials
- [ ] See maker navigation only (not checker/reviewer/admin items)
- [ ] Upload a regulation PDF document
- [ ] See extraction status as pending then processing then completed
- [ ] Click Extract Now on a pending document — status updates
- [ ] View extracted points for a completed document
- [ ] Upload an internal document
- [ ] Create a new library using 3-column interface
- [ ] Column 1 shows regulation docs with completed status
- [ ] Clicking doc in column 1 loads its points in column 2
- [ ] Checking points in column 2 adds them to column 3
- [ ] Column 3 health panel shows correct counts
- [ ] Save library with name and department
- [ ] Run new analysis using a library
- [ ] Run new analysis using manual point selection
- [ ] Progress page shows X/Y points updating in real time
- [ ] Navigate away during analysis then come back — same files and points shown
- [ ] Resume button continues from where it left off
- [ ] Results page shows compliant/partial/non-compliant points
- [ ] Progress shows 62/63 format correctly
- [ ] Points with dual verify failed show the badge
- [ ] Rerun dual verify for one failed point (only dual verify reruns)
- [ ] Rerun a failed landing AI point (both reruns)
- [ ] Rerun all failed dual verifications button works
- [ ] Edit action plan on a partial compliant point
- [ ] Original action plan is unchanged after edit
- [ ] View Change History shows version 1 (AI original) and version 2 (edit)
- [ ] Use This Version button restores a previous version
- [ ] New history entry created when version restored
- [ ] Export PDF works and includes all points
- [ ] Export Excel works and includes all points
- [ ] Submit for checker review — status changes

## Checker

- [ ] Login with checker credentials
- [ ] See only checker navigation
- [ ] Review queue shows submitted analysis
- [ ] Open review — see all points read-only
- [ ] Add comment on a specific point
- [ ] Add overall comment
- [ ] Pull back — requires comment, status changes to pulled_back
- [ ] Approve — status changes to checker_approved
- [ ] Maker sees pulled_back analysis with comments visible

## Reviewer

- [ ] Login with reviewer credentials
- [ ] See only reviewer navigation
- [ ] Final review queue shows checker_approved analyses
- [ ] See full history including checker comments
- [ ] Pull back to checker — goes back to checker queue
- [ ] Finalize — status changes to reviewer_approved

## All Roles

- [ ] Forgot password sends email
- [ ] Reset password link from email works
- [ ] `/old` route shows Angular app (requires `BCP_WEB_URL` and running `bcp-web`)
- [ ] Wrong role cannot access other role pages
- [ ] Unauthenticated user redirected to login
