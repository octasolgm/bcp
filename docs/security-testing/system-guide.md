# Comply Solutions - System Guide

Comply Solutions checks a bank's internal policy documents against a regulation, point by point. It
finds the gaps, suggests corrective actions, and runs the result through a maker / checker / reviewer
approval flow. When the report is finalized, the system writes the approved fixes into a new version
of the internal document.

- **URL:** https://bcp-web-dev.azurewebsites.net/
- **Login:** the super admin credentials shared with you separately.
- **Ground rules:** do not delete or change anything that already exists. You are free to upload,
  create, edit and delete your **own** items. Please put `SEC-TEST` at the start of anything you
  create (workspaces, users, documents, libraries) so it is easy to tell apart.

## Quick start - the main flow in short

1. **Documents > Regulation Documents** - upload `CBUAE_EN_3945_VER2 azure-1-36`, click **Parse**, then **Extract**.
2. **Documents > Internal Documents** - upload `Internal - Implementation of AML CFTPF Manual (1)`, click **Parse**, then **Extract**.
3. **Analysis > New analysis** - tick the CBUAE regulation, select its points (clauses 3.1 to 3.5 are enough for a quick test), tick the AML CFTPF Manual and click **Run analysis**.
4. **Analysis > All analysis** - open the finished run to see the gap analysis report.
5. Work the report - edit gaps, add actions, add reviews, mark actions resolved.
6. **Submit for review** - maker sends to checker, checker sends to reviewer.
7. **Reviewer > Finalize** - the report is signed off and a new corrected version (v2) of the internal document appears under Internal Documents.

The sections below explain each step and every other feature in more detail.

---

## 1. Roles

| Role | What they can do |
|---|---|
| **Super admin (platform)** | Everything: all workspaces, users, limits, platform settings, AI models, prompts. Can act as maker, checker and reviewer. This is the login you have. |
| **Workspace admin** | Manages users and departments of their own workspace only. |
| **Maker** | Uploads documents, runs analyses, edits gaps and actions, submits the report for review. |
| **Checker** | Reviews the maker's report, approves or asks for changes, sends it to the reviewer or back to the maker. |
| **Reviewer** | Final review. Can send it back, or **Finalize** the report. |

## 2. Workspaces (clients) and limits

Each client bank has its own workspace. Users, documents and analyses in one workspace cannot be seen
from another workspace.

**Create a workspace**

1. **Admin settings > Business workspaces**.
2. Fill in **Workspace name** (e.g. `SEC-TEST Bank`), short name and description.
3. Optional: under **Workspace admin** enter a full name, email and initial password to create the
   workspace's admin at the same time.
4. Click **Create workspace**.

**Manage a workspace** (on its row in the list)

- **Open** - switch into that workspace. Everything you see now (documents, analyses, users) belongs to it.
- **Users** - list the users of that workspace.
- **Credits** - credit balance, AI cost, amount billed and spend by model.
- **Rename**, **Deactivate / Activate**.

**Set limits** (under **Credits** on the workspace row)

- **Spend limit** - **No limit** or **Set limit** in dollars or credits, then **Save spend limit**.
- **Clause limit** - total clauses this workspace may analyse (re-runs are not counted), then **Save clause limit**.
- New analyses stop when either limit is reached.
- **Margin** - default or a custom markup on AI cost, then **Save margin**.
- **Add credits** - top up the workspace's credit balance.
- **Date format** for the workspace.

Platform-wide **AI credit price** (what one credit is worth and the default markup) is set at the top
of the same page.

Tip: to test isolation, create a `SEC-TEST` workspace, **Open** it, create users inside it, upload
documents there, then confirm none of it is visible from the default workspace and the other way around.

## 3. Users, roles and departments

**Create a user**

1. **Admin settings > User management** (users are created in the workspace you are currently in).
2. Click **Create user**, enter full name, email, **Role** (Maker, Checker or Reviewer), optional
   **Department** and an optional initial password.
3. Save. The user can sign in straight away with that password.

**Manage users** (on each row): change role, change department, **Set password**,
**Deactivate / Activate**, **Delete**. Filter by role or status (Active, Deactivated, Pending invitation).

**Departments:** **Admin settings > Departments** - create, edit, activate / deactivate and delete
departments. Users, documents and action owners can be linked to a department.

To test the full approval flow with real roles: create one Maker, one Checker and one Reviewer
(`SEC-TEST maker` etc.), then **Sign out** (top right) and sign in as each one in turn.

## 4. Documents

### Regulation documents

1. **Documents > Regulation Documents** > **+ Upload regulation** (PDF or Word).
2. **Parse** - reads the file into text with page references. Wait for **Parsed**.
3. **Extract** - pulls out the individual regulation points. Wait for **Extracted**.
4. **View extracted points** - see the points by chapter and clause.
5. You can also add custom points manually in the manual entry row, **Download** or **Delete** your own documents.

### Internal documents

1. **Documents > Internal Documents** > **+ Upload** (PDF or Word).
2. **Parse**, then **Extract** (splits the policy into sections). Wait for **Extracted**.
3. Each document shows its status, who uploaded / parsed it, and **History** - the analyses it was used
   in and the embedded actions history (see Finalize below).
4. Corrected versions created by Finalize appear here as new versions, e.g. `<name> (v2)`.

Both pages also have **Import from cloud** to pull files from a connected cloud storage account
(see Integrations).

### Regulation Points Library

**Documents > Regulation Points Library** > **+ New library** - give it a name and description, pick
points from one or more regulations and save. A library is a reusable set of points that can be chosen
on the New analysis page instead of picking points every time. **View points**, **Edit**, **Delete**.

## 5. Run an analysis

1. **Analysis > New analysis**.
2. **Step 1 - Regulation Sources:** choose the **Regulation Documents** tab (or **From Regulation Points
   Library**) and tick the regulation / library.
3. **Regulation points:** tick the points to analyse (search, select a whole chapter, or **Select all**).
4. **Step 2 - Internal Documents:** tick one or more internal documents.
5. **Summary & run analysis** > **Run analysis**.
6. Follow live progress point by point. Running analyses are listed under **Analysis > In progress**,
   and can be stopped from the analysis list.

Each point gets a status (**compliant**, **partially compliant** or **non compliant**), a confidence
score, the matching policy extract and document reference, the gaps found and suggested corrective
actions. Points that failed can be re-run individually, or use **Rerun all**.

## 6. Work on the analysis report (gaps, actions, reviews)

Open a run from **Analysis > All analysis**. The gap analysis report shows the points on the left and
the selected point's detail on the right. Filter by status or risk, switch between card and list view.

**For each point you can see:** the regulatory requirement, the policy extract, document reference,
status and compliance confidence %, and the **Corrective Action Plan**.

**Gaps**

- **+ Add gap** - add a gap the AI missed.
- **Edit** a gap's text, risk and priority, then **Save gap**.
- **Full history** - every edit is versioned (who and when); **Set as current** restores an older version.

**Actions** (each gap can have several)

- **+ Add action** - action text, one or more owners (people or departments), target date, priority.
- **Edit**, **Delete**, **Mark resolved** / **Reopen**.
- **Status history** and **Target date history** (a reason is required when a target date changes).

**Reviews** (comments on an action)

- **+ Add review** - write a comment. Optionally **Send review to** a department or a person; it lands
  in their **Notifications** inbox.
- Checker and reviewer use reviews to **Approve** or ask to **Need modify**.

**Evidence**

- **Upload supporting docs** on a point (or on a single gap), then **Rerun analysis (this point)** /
  **Rerun this gap** to re-check the gap against the new evidence.
- **Overall analysis report** panel: upload a document for the whole analysis and **Rerun all gaps**.
- Results show what is now covered and what is still missing.

**Export report** - Excel (choose sheets and columns: gap analysis, actions, reviews, evidence re-check)
or PDF (the full gap analysis narrative).

## 7. Approval flow: maker > checker > reviewer > finalize

1. **Maker** - after working the report, clicks **Submit for review** (**Submit to** checker).
2. **Checker** - finds it under **Pending reviews > Pending review**, opens it, adds reviews on actions
   (**Approve** or **Need modify**), adds a point or overall comment, then:
    - **Send to reviewer**, or
    - **Pull back to maker** if changes are needed. The maker finds it under
      **Pending reviews > Pending correction**, fixes it and clicks **Resubmit for review**.
3. **Reviewer** - finds it under **Pending reviews > Pending final review**, reviews it, then:
    - **Pull back to checker** / **Pull back to maker**, or
    - **Finalize**.
4. A sender can **Recall** a run before the next person acts on it.

The run's status moves through: Ready for review (maker) > Submitted for review (checker) >
Checker approved (reviewer) > Finalized.

As super admin you can play every role yourself, or use the Maker / Checker / Reviewer users you
created (section 3) to test that each role only sees and does what it should.

## 8. Finalize - corrected internal document

When the reviewer clicks **Finalize**:

- Every **resolved** action is turned into proper policy wording by AI and written into the internal
  document at the relevant page / section, marked as **POLICY UPDATE - Regulatory Clause ...**.
- The result is saved as a **new version** of the internal document (e.g. `AML CFTPF Manual (v2)`)
  under **Documents > Internal Documents**. The original file is never changed.
- The document's **History** shows each embedded update: clause, gap, action and the text that was added.

To test: resolve at least one action (**Mark resolved**) before finalizing, then open the new v2 file.

## 9. Other features

| Where | What |
|---|---|
| **Overview** | Dashboard of compliance status, gaps by priority (high / medium / low) and outstanding actions. |
| **Notifications** (top bar) | Reviews and items routed to you. |
| **Analysis > Analysis Version** | Choose between analysis versions / workflows. |
| **Analysis > Evals** | Saved reference results to compare analyses against (**Save clauses as evals**, **Compare with evals** on a report). |
| **Admin settings > Integrations** | Connect cloud storage (e.g. Google Drive, read-only) and use **Import from cloud** on document pages. |
| **Admin settings > Platform settings** | Choose the AI models: main analysis model, dual verify model, finalize model. |
| **Admin settings > Analysis prompts** | View and version the AI prompts, add improvement suggestions, generate a new version with AI. |
| **Admin settings > Query dictionary / Query synonyms** | Terms used to improve how policy text is matched to regulation points. |
| **Admin settings > Deleted analyses** | Restore soft-deleted analyses (please only restore / delete your own). |
| **Billing > AI usage** | AI calls, tokens and cost per workspace, model and activity. |
| **Billing > AI credits** | Credit balance, used, added, and full credit activity. |

Please do not change **Platform settings**, **Analysis prompts**, **Query dictionary** or
**Query synonyms**, as they affect every workspace. Viewing them is fine.

## Contact

For access issues or questions during testing, reach out to GM (development, Comply Solutions).
