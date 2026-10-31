# Comply Solutions - managing sales and development in Jumppl

The operating guide for the Comply Solutions team in Jumppl: how the client, the sales team and the development
team work together to sell the Comply Solutions compliance platform to banks and to deliver it once a bank signs.

Last updated: 29 Oct 2026. Workspace: **Ultar Tech**.

---

## Contents

1. The setup at a glance
2. Who does what
3. The Sales project: structure, statuses, bank details
4. File storage
5. The sales process from lead to onboarding
6. How to: step-by-step for the client, the sales team and the development team
7. Working between sales and development
8. Routines: daily, weekly, monthly
9. Tracking and reports
10. Rules everyone follows
11. Setup status and what is left
12. Questions and answers

---

## 1. The setup at a glance

```
                          ULTAR TECH WORKSPACE (Jumppl)
+-----------------------------------------------------------------------------------+
|                                                                                   |
|  PROJECTS                                    FILE STORAGE                         |
|  +--------------------------------+          +--------------------------------+   |
|  | Sales Comply Solutions (33094) |          | Sales Folder Comply Solutions  |   |
|  |  UAE / Saudi Arabia / Pakistan |          |  01 Sales collateral ...       |   |
|  |  > bank types > one task/bank  |          |  ... 07 Meeting notes          |   |
|  +--------------------------------+          +--------------------------------+   |
|  +--------------------------------+          +--------------------------------+   |
|  | Comply Solution (33014)        |          | Comply-Solution                |   |
|  |  product development tasks     |          |  development files             |   |
|  +--------------------------------+          +--------------------------------+   |
|                 |                                          |                      |
|                 +--------------------+---------------------+                      |
|                                      v                                            |
|                 +---------------------------------------------+                   |
|                 | CLIENT PORTAL  "Comply-Solution"            |                   |
|                 | Projects tab: both projects                 |                   |
|                 | Files tab:    both folders                  |                   |
|                 | (client.jumppl.com)                         |                   |
|                 +---------------------------------------------+                   |
|                                                                                   |
|  GROUPS: Sales Group Comply Solutions  |  Development Group Comply Solution       |
+-----------------------------------------------------------------------------------+
```

| Item | Link | Shared with |
|---|---|---|
| Project **Sales Comply Solutions** | https://app.jumppl.com/portal/task/33094 | Sales Group Comply Solutions, Comply-Solution portal |
| Project **Comply Solution** (development) | https://app.jumppl.com/portal/task/33014 | Development Group Comply Solution, Comply-Solution portal |
| File Storage **Sales Folder Comply Solutions** | https://app.jumppl.com/portal/docsdetail/a59cf312-27d7-4f81-8e08-2a751591e6c2 | Everyone@Ultar Tech, Sales Group Comply Solutions, Comply-Solution portal |
| File Storage **Comply-Solution** (development files) | (the portal's own folder) | Comply-Solution portal |
| Client portal **Comply-Solution** | https://client.jumppl.com/account/join/portal/9fd939b9-f3d2-420a-b8c5-a9ede45200d7 | Portal members (client and team) |

**One client portal for everything**: the client sees both projects and both folders in the Comply-Solution
portal. Sales and development stay apart because they are separate projects and separate folders, not separate
portals.

| Group | Members (29 Oct 2026) |
|---|---|
| Sales Group Comply Solutions | Hammad Mujtaba, Hania Yasir, GM Rehman, Shahid M |
| Development Group Comply Solution | Nasir Mahboob, Hammad Mujtaba, GM Rehman, Shahid M |

---

## 2. Who does what

| Who | Works in | Owns | Does |
|---|---|---|---|
| **Client (Comply Solutions)** | Client portal "Comply-Solution" | The list of target banks | Adds banks and contacts, moves banks through the pipeline, comments, shares bank documents, follows development progress |
| **Sales team** | Jumppl: Sales Comply Solutions | Each bank assigned to them | Contacts banks, books meetings, runs demos with development, sends proposals, negotiates, keeps every bank up to date |
| **Development team** | Jumppl: Comply Solution | Demo, pilot and onboarding tasks | Prepares demo accounts, supports pilots, answers technical and security questions, sets up each won bank |
| **Pipeline owner** (one person, e.g. GM Rehman) | Both projects | The process itself | Runs the Monday review, keeps the structure clean, adds countries and bank types, assigns new leads |

The two rules that hold it together:
1. **Sales information lives in the Sales project, product work lives in the development project.**
2. **One bank = one task, with one owner and one next date.**

---

## 3. The Sales project: structure, statuses, bank details

### 3.1 Structure: country > bank type > bank

```
Sales Comply Solutions                              <- level 1: pipeline statuses set here
  |
  +-- UAE                                           <- level 2: country
  |     +-- Commercial banks                        <- level 3: bank type
  |     |     +-- Task: "Bank A"                    <- one task per bank
  |     |     |     +-- Subtask: Intro call with Head of Compliance
  |     |     |     +-- Subtask: Demo (platform walkthrough)
  |     |     |     +-- Subtask: Send proposal
  |     |     +-- Task: "Bank B"
  |     +-- Islamic banks
  |     +-- Foreign bank branches
  |     +-- Exchange houses and finance companies
  |
  +-- Saudi Arabia
  |     +-- Local banks
  |     +-- Foreign bank branches
  |     +-- Finance companies
  |
  +-- Pakistan
        +-- Commercial banks
        +-- Islamic banks
        +-- Microfinance banks
```

- Every country and bank type uses the same pipeline statuses (set once on the Sales project).
- **New country**: add a level 2 sub-project with the country name, and add the bank fields to it (3.4). Add bank types (level 3) once it has several
  banks; with 2-3 banks, put the bank tasks straight under the country.
- Each country gets the bank types of its own market; they do not have to match the UAE list.
- Choose the right bank type when adding a bank: changing it later means moving the task.

### 3.2 A bank task

| Part of the task | Used for |
|---|---|
| **Name** | The bank's official name, written the same way everywhere (search and Jumppl AI find it by name) |
| **Status** | Where the bank is in the pipeline (3.3) |
| **Assignee** | The salesperson who owns the bank. Always exactly one |
| **Due date** | The date of the **next step** (call back, meeting, send proposal). Never empty while the bank is open |
| **Priority** | Normal / Important / Critical = how hot the deal is |
| **Subtasks** | The individual steps: calls, demo, proposal, questionnaire, each with an owner and date |
| **Comments** | Every conversation, meeting note and decision, newest at the bottom. Use @mentions to pull someone in |
| **Attachments** | The key files for this bank (proposal sent, NDA, RFP); full set in `04 Banks/<bank>` |
| **Custom fields** | The bank's details (3.4) |
| **Tags** | Short labels: `demo-requested`, `rfp`, `referral`, `budget-approved`, `decision-q1` |

### 3.3 Pipeline statuses

Set up on 29 Oct 2026, in this order:

| # | Status | Means | Move here when | Next step |
|---|---|---|---|---|
| 1 | **Lead** | Bank is on the list, not contacted | The bank is added | Contact within 5 working days |
| 2 | **Contacted** | First email or call done | The first message went out | Follow up after 3-5 days |
| 3 | **Meeting scheduled** | Intro meeting booked | A date is agreed | Prepare; request a demo if they want one |
| 4 | **Demo done** | The platform was shown | The demo took place | Send the proposal or agree a pilot |
| 5 | **Proposal sent** | Pricing and scope sent | The proposal file went out | Follow up within a week |
| 6 | **Negotiation** | Price, scope, security or legal discussion | The bank responds with questions or changes | Answer each question as a subtask |
| 7 | **Pilot / POC** | Bank tests on its own documents | A pilot is agreed | Development supports; collect feedback |
| 8 | **Won** | Contract signed | Signature | Hand over to development (section 7) |
| 9 | **Lost** | Bank said no | A clear no | Write the reason; archive later |
| 10 | **On hold** | Bank asked to come back later | "Not now" | Due date = when to try again |
| - | Archived | Out of the way | A lost bank's reason is recorded | None |

- Jumppl allows only one "completed" status per project: **Won** is the completed one. Lost banks count as open
  until they are set to Archived.
- Statuses cannot be deleted, only renamed (project status menu).
- The **Kanban board** of the Sales project shows one column per status: this is the pipeline view.

### 3.4 Bank details (custom fields)

A bank is one task, and **its details are stored in custom fields on that task**. The task name holds only the bank's
name; everything else (city, contacts, deal value, dates) goes into these fields. Country and bank type are not
fields: they come from the sub-project the bank sits in.

#### The fields

| Field | Field type in Jumppl | Example / options |
|---|---|---|
| City | text | Dubai, Riyadh, Karachi |
| Bank website | url | https://www.bank-a.com |
| Main contact | text | The decision maker's name |
| Designation | text | Head of Compliance, MLRO, CRO, CIO |
| Contact email | email | |
| Contact phone | phone | |
| Other contacts | textarea | One line per person: name, role, email, phone |
| Lead source | dropdown | Referral, Event, LinkedIn, Partner, Inbound |
| Estimated deal value | currency | USD (or the currency the client prices in) |
| Expected close date | date | |
| Demo account given | dropdown | Yes, No |
| Next follow-up | date | |

Jumppl has no checkbox field, so "Demo account given" is a Yes/No dropdown.

#### Set up (29 Oct 2026)

All 12 fields are created and linked to the Sales project **and all 13 country and bank-type sub-projects**
(checked: every level returns the same 12 fields). Settings: shown in the client portal, shown as columns in the
task list, also shown on subtasks. "Lead source" has Referral / Event / LinkedIn / Partner / Inbound,
"Demo account given" has Yes / No, "Estimated deal value" is in USD.

Important: in Jumppl a custom field belongs to the projects it is linked to, and a task in a sub-project only shows
the fields linked to **that sub-project**. So when a **new country or bank type** is added, the fields have to be
added to it as well: open the new sub-project > project menu > Custom Fields > Manage Custom Fields and add the same
12 fields with the same names and types (or ask for them to be linked).

#### Where the details are entered

```
Bank task "Bank A" (opened)
+-----------------------------------------------------------+
| Bank A                                    Status: Contacted |
| Assignee: Hania Yasir          Due: Tue 3 Nov (next step)   |
|-----------------------------------------------------------|
| Custom fields                                             |
|   City ................ Dubai                             |
|   Bank website ........ https://www.bank-a.com            |
|   Main contact ........ Ahmed Khan                        |
|   Designation ......... Head of Compliance                |
|   Contact email ....... ahmed.khan@bank-a.com             |
|   Contact phone ....... +971 4 000 0000                   |
|   Other contacts ...... Sara Ali, MLRO, sara.ali@...      |
|   Lead source ......... Referral                          |
|   Est. deal value ..... USD 40,000                        |
|   Expected close ...... 15 Jan 2027                       |
|   Demo account given .. No                                |
|   Next follow-up ...... 3 Nov 2026                        |
|-----------------------------------------------------------|
| Subtasks: Intro call, Demo, Proposal                      |
| Comments: meeting notes, every conversation               |
| Attachments: NDA, proposal                                |
+-----------------------------------------------------------+
```

- **Add a bank**: create the task with the bank's name, then open it and fill the custom fields.
- **Update a bank**: open the task and change the fields; the task's history keeps what changed.
- **See all banks with their details**: the project's list view shows the fields as columns.
- **Bulk**: the CSV import (3.6) fills the fields from the sheet's columns.

### 3.5 Full example: one bank from start to finish

A made-up bank, **Gulf Horizon Bank** (not a real bank), filled in completely. Copy this as the model for the
sample bank task (checklist item 8).

#### Where it sits

```
Sales Comply Solutions > UAE > Commercial banks > Task: "Gulf Horizon Bank"
```

#### The task

| Part | Value |
|---|---|
| Task name | Gulf Horizon Bank |
| Status | Proposal sent |
| Assignee | Hania Yasir |
| Due date | Tue 10 Nov 2026 (follow up on the proposal) |
| Priority | Important |
| Tags | `referral`, `demo-requested`, `decision-q1` |
| Description | Mid-size UAE commercial bank, about 40 branches. Compliance team of 12. Main pain: manual gap analysis of CBUAE AML rules against their internal policies before inspections. Wants action plans tracked per gap. |

#### Custom fields

| Field | Value |
|---|---|
| City | Dubai |
| Bank website | https://www.gulfhorizonbank.example |
| Main contact | Ahmed Khan |
| Designation | Head of Compliance |
| Contact email | ahmed.khan@gulfhorizonbank.example |
| Contact phone | +971 4 555 0101 |
| Other contacts | Sara Ali, MLRO, sara.ali@gulfhorizonbank.example, +971 4 555 0102<br>Omar Haddad, CIO, omar.haddad@gulfhorizonbank.example (IT and hosting questions) |
| Lead source | Referral |
| Estimated deal value | USD 45,000 |
| Expected close date | 15 Jan 2027 |
| Demo account given | Yes |
| Next follow-up | 10 Nov 2026 |

#### Subtasks

| Subtask | Owner | Due | Status |
|---|---|---|---|
| Intro call with Ahmed Khan | Hania Yasir | 12 Oct 2026 | Done |
| Demo: AML gap analysis on sample policy | Hania Yasir | 20 Oct 2026 | Done |
| Give demo account to Sara Ali (2 weeks) | Hania Yasir | 21 Oct 2026 | Done |
| Send proposal | Hania Yasir | 29 Oct 2026 | Done |
| Answer IT hosting questions from Omar Haddad | GM Rehman | 6 Nov 2026 | Open |
| Follow up on proposal | Hania Yasir | 10 Nov 2026 | Open |

#### Comments (oldest first)

```
5 Oct  - Hania Yasir:  Lead from referral by a compliance consultant. Sent intro email to Ahmed Khan.
          Status -> Contacted.
9 Oct  - Hania Yasir:  Ahmed replied, intro call booked for 12 Oct. Status -> Meeting scheduled.
12 Oct - Hania Yasir:  Call done. They prepare for a CBUAE inspection in Q1 and do gap analysis by hand
          in Excel. Want to see gap analysis and action plans. Asked for a demo; requested
          "Demo - Gulf Horizon Bank" in Comply Solution for 20 Oct. @GM Rehman
20 Oct - Hania Yasir:  Demo done with Ahmed and Sara. Liked the gap report and action plans per gap;
          asked about on-premise hosting. Demo account given to Sara for 2 weeks. Status -> Demo done.
29 Oct - Hania Yasir:  Proposal sent: 1 workspace, 15 users, USD 45,000 per year. File attached and in
          03 Proposals and pricing. Status -> Proposal sent.
30 Oct - Omar Haddad's questions (data residency, SSO) added as a subtask for @GM Rehman.
```

#### Attachments and files

| File | Attached to task | Saved in |
|---|---|---|
| Gulf Horizon Bank - NDA - 2026-10-14.pdf | Yes | `05 Contracts and NDAs` and `04 Banks/Gulf Horizon Bank` |
| Gulf Horizon Bank - Meeting notes - 2026-10-12.docx | No | `04 Banks/Gulf Horizon Bank` |
| Gulf Horizon Bank - Proposal - 2026-10-29.pdf | Yes | `03 Proposals and pricing` |
| Gulf Horizon Bank - IT questionnaire - 2026-10-30.xlsx | Yes | `04 Banks/Gulf Horizon Bank` |

#### Development tasks for this bank (in Comply Solution)

| Task | Assignee | Due | Description |
|---|---|---|---|
| Demo - Gulf Horizon Bank | GM Rehman | 20 Oct 2026 | Demo for Ahmed Khan (Head of Compliance) and Sara Ali (MLRO). Show: upload a policy, gap analysis against CBUAE AML rules, action plans per gap. Demo account for Sara for 2 weeks. Bank task: Sales Comply Solutions > UAE > Commercial banks > Gulf Horizon Bank |
| Pilot - Gulf Horizon Bank | (when agreed) | | Created only if the bank asks for a pilot |
| Onboard - Gulf Horizon Bank | (when Won) | | Created only when the bank signs |

#### What happens next

| If | Then |
|---|---|
| The bank asks for a pilot | Status Pilot / POC; sales creates "Pilot - Gulf Horizon Bank" with start and end dates |
| The bank negotiates price | Status Negotiation; each question becomes a subtask |
| The bank signs | Status Won; contract in `05 Contracts and NDAs`; "Onboard - Gulf Horizon Bank" in Comply Solution; Feed post |
| The bank says no | Comment with the reason (e.g. "chose another vendor on price"); status Lost, later Archived |
| The bank says "after the inspection" | Status On hold; due date = 1 Apr 2027; comment why |

#### The same bank as a CSV row (for importing many banks, 3.6)

```
Bank name,City,Bank website,Main contact,Designation,Contact email,Contact phone,Lead source,Estimated deal value,Expected close date
Gulf Horizon Bank,Dubai,https://www.gulfhorizonbank.example,Ahmed Khan,Head of Compliance,ahmed.khan@gulfhorizonbank.example,+971 4 555 0101,Referral,45000,2027-01-15
Desert Pearl Islamic Bank,Abu Dhabi,https://www.desertpearl.example,Fatima Noor,MLRO,fatima.noor@desertpearl.example,+971 2 555 0200,Event,30000,2027-03-31
```

(Both banks are made up. The second one would go in UAE > Islamic banks.)

### 3.6 Bringing in an existing list of banks

1. Save one CSV per country and bank type, one row per bank (bank name, city, contact, email, phone, ...).
2. Open that bank type's sub-project (for example UAE > Islamic banks): menu > **Import Tasks from CSV**.
3. Choose the bank name column as the task name and map the other columns to the custom fields.
4. Imported banks start as **Lead**. The pipeline owner then assigns each bank and sets a due date.

---

## 4. File storage

### 4.1 Sales Folder Comply Solutions

```
Sales Folder Comply Solutions
  01 Sales collateral        brochure, one-pager, pitch deck, product video links
  02 Demo                    demo script, demo account guide, demo recordings
  03 Proposals and pricing   proposal template, price list, sent proposals (one per bank)
  04 Banks                   one folder per bank: NDA, RFP, questionnaires, meeting notes
  05 Contracts and NDAs      templates and signed copies
  06 Security and compliance security questionnaire answers, hosting and data protection notes
  07 Meeting notes           pipeline review notes
```

### 4.2 Comply-Solution (development files)

The client portal's development folder: meeting notes, weekly summaries and other development material. No sales
files go here.

### 4.3 Naming files

- Bank folders in `04 Banks`: the bank's official name, same as its task (`04 Banks/Bank A`).
- Files: `<bank> - <what> - <yyyy-mm-dd>`, for example `Bank A - Proposal - 2026-11-03.pdf`.
- A new version is a new date, never "final2".

### 4.4 Who sees what

| Folder | Seen by |
|---|---|
| Sales Folder Comply Solutions | Everyone in Ultar Tech, Sales Group Comply Solutions, the client through the portal |
| Comply-Solution | The client and team through the portal |

The client sees everything in both folders. Keep internal drafts and internal price discussions out of them (for
example in a section shared only with Sales Group Comply Solutions). The sales folder is also shared with
**Everyone@Ultar Tech**: remove that share if only the sales team and the client should see it.

---

## 5. The sales process from lead to onboarding

```
+-----------+    +-----------+    +-------------+    +-----------+    +---------------+
| 1 Lead    | -> | 2 Contact | -> | 3 Meeting   | -> | 4 Demo    | -> | 5 Proposal    |
| client or |    | sales     |    | scheduled   |    | sales +   |    | sales sends,  |
| sales adds|    | emails or |    | (calendar   |    | dev team  |    | file in       |
| the bank  |    | calls     |    | event)      |    | (demo acc)|    | 03 Proposals  |
+-----------+    +-----------+    +-------------+    +-----------+    +---------------+
                                                                              |
      +-----------------------------------------------------------------------+
      v
+---------------+    +-------------+    +----------------------------------------------+
| 6 Negotiation | -> | 7 Pilot/POC | -> | 8 Won -> "Onboard - <bank>" task in the       |
| price, legal, |    | bank tests  |    |   development project: the bank's workspace,  |
| security Q&A  |    | own docs    |    |   admin users, first regulations, go-live     |
+---------------+    +-------------+    +----------------------------------------------+
        At any step: 9 Lost (reason in a comment) or 10 On hold (due date = when to try again)
```

| Step | Who | What happens in Jumppl |
|---|---|---|
| 1 Lead | Client or sales | Bank task under the right country and bank type, fields filled, status Lead, owner and due date set |
| 2 Contacted | Sales | Comment with what was sent and to whom; due date = follow-up date |
| 3 Meeting scheduled | Sales | Calendar event for the meeting; agenda in a comment; status Meeting scheduled |
| 4 Demo | Sales + development | Sales creates "Demo - <bank>" in the development project (section 7). After the demo: notes in a comment, "Demo account given" ticked, status Demo done |
| 5 Proposal sent | Sales | Proposal in `03 Proposals and pricing` and attached to the bank task; deal value and close date filled |
| 6 Negotiation | Sales (+ development) | Each open question is a subtask with an owner and date; technical ones assigned to development |
| 7 Pilot / POC | Sales + development | "Pilot - <bank>" in the development project with start and end dates; sales collects feedback in comments |
| 8 Won | Sales, then development | Status Won; "Onboard - <bank>" in the development project; contract in `05 Contracts and NDAs`; Feed post |
| 9 Lost | Sales | Reason in a comment (price, timing, competitor, no budget, other); archive once recorded |
| 10 On hold | Sales | Reason in a comment; due date = the date to try again |

---

## 6. How to: step by step

### 6.1 The client (in the client portal)

**Sign in**
1. Open client.jumppl.com and sign in (first time: use the portal invitation link).
2. The Comply-Solution portal opens. **Projects** shows Sales Comply Solutions and Comply Solution; **Files**
   shows both folders.

**Add a bank**
1. Projects > Sales Comply Solutions > the country > the bank type.
2. Add a task with the bank's official name (only the name).
3. Open the new task. In its **Custom fields** area fill what you know: city, website, main contact,
   designation, email, phone, other contacts, lead source (3.4).
4. Leave the status on **Lead**. The pipeline owner assigns it to a salesperson.

**Update a bank**
- Change the status when something happens, or drag the bank to the next column on the board.
- Add a comment with what happened (who you spoke to, what they said).
- Add contact persons in "Other contacts" or as a comment.

**Share a document from a bank**
- Attach it to the bank's task, or upload it to `Sales Folder Comply Solutions/04 Banks/<bank>`.

**Follow development**
- Projects > Comply Solution shows the development tasks and their statuses; comment on a task to ask about it.

### 6.2 The sales team (in Jumppl)

**Start of day**
1. My Tasks: work the overdue and due-today banks first.
2. For each: do the step, comment what happened, set the next due date, change the status if it moved.

**Take over a new lead**
1. Open the bank task and check its custom fields (3.4); add what is missing.
2. Set yourself as the assignee and a due date for the first contact.

**Book a meeting**
1. Create a calendar event with the bank's name in the title.
2. Status: Meeting scheduled. Agenda in a comment.

**Ask for a demo**
1. In the development project create "Demo - <bank>", due on the demo date, assigned to the developer (or to
   the development lead to assign).
2. Describe: who attends, what they care about (e.g. AML gap analysis, action plans), demo account needed or not.
3. Link it: a comment on the bank task naming the demo task.

**Send a proposal**
1. Save it in `03 Proposals and pricing` as `<bank> - Proposal - <date>` and attach it to the bank task.
2. Fill "Estimated deal value" and "Expected close date". Status: Proposal sent.

**Close a bank**
- Won: status Won, then section 7 (handover), then a Feed post.
- Lost: reason in a comment, status Lost, later Archived.
- On hold: reason in a comment, status On hold, due date = when to try again.

### 6.3 The development team (in Jumppl)

- **Demo / Pilot / Onboard tasks** arrive in the Comply Solution project with a bank name and a due date. They
  follow the development project's own statuses (Pending, In Progress, ..., Done).
- Before a demo: prepare the demo account and sample data, confirm in a comment on the demo task.
- During a pilot: track the bank's issues as subtasks of the pilot task.
- For onboarding: create the bank's workspace, admin users and first regulations; set the onboarding task to Done
  and comment the go-live date, so sales can tell the client.
- Security or hosting questions from a bank: answer in the subtask sales created, and save reusable answers in
  `06 Security and compliance`.

---

## 7. Working between sales and development

```
Sales Comply Solutions                         Comply Solution (development)
+---------------------------+   creates        +----------------------------------+
| Bank A   (Meeting sched.) | ---------------> | Demo - Bank A      due demo date  |
| Bank A   (Pilot / POC)    | ---------------> | Pilot - Bank A     start-end      |
| Bank A   (Won)            | ---------------> | Onboard - Bank A   go-live date   |
+---------------------------+                  +----------------------------------+
            ^                                                  |
            +------------- comment "done" / questions ---------+
```

- Task names always start with **Demo -**, **Pilot -** or **Onboard -** followed by the bank's official name, so
  both teams (and Jumppl AI) can find them.
- The sales owner creates the development task and stays its follower; development owns doing it.
- Every development task names the bank task in its description, and the bank task gets a comment naming the
  development task.
- Development never changes a bank's sales status; sales never changes a development task's status.

---

## 8. Routines

| When | Who | What |
|---|---|---|
| **Daily** | Sales | My Tasks: overdue and due today first; every touched bank gets a comment and a new due date |
| **Monday** | Client + sales + pipeline owner | Pipeline review on the Kanban board, column by column: every open bank has an owner and a future due date; stuck banks (no update for 14 days) get a decision |
| **Monday** | Sales + development | Upcoming demos, pilots and onboardings for the week |
| **After a win or a key demo** | Sales | Feed post so the client and both teams see it |
| **Monthly** | Pipeline owner + client | Banks won and lost and why, banks per status per country, new countries or bank types, clean-up (archive old Lost banks) |

Save the Monday review notes in `07 Meeting notes`.

---

## 9. Tracking and reports

| Want to see | Where |
|---|---|
| The whole pipeline | Sales project > Kanban board (one column per status) |
| One country | Open the country sub-project |
| My banks for today | My Tasks (overdue and due today) |
| Banks nobody touched | List view sorted by last update, or ask Jumppl AI |
| Pilot and onboarding dates | Gantt view of the development project |
| Status totals | Reports, or the project overview |

**Jumppl AI** (the assistant in Jumppl) can answer and prepare changes, for example:

- "Show banks in Proposal sent that were not updated for 14 days"
- "Which UAE banks are in Negotiation?"
- "Add Bank X to UAE > Islamic banks, contact Ahmed, Head of Compliance"
- "Move Bank X to Demo done and comment: demo went well, they want a pilot"
- "Create a task in Comply Solution: Demo - Bank X on Tuesday, assign it to GM Rehman"
- "Weekly status report for Sales Comply Solutions"

Changes are shown as a card first; nothing is saved until you press Confirm.

---

## 10. Rules everyone follows

1. One bank = one task, named with the bank's official name.
2. Every open bank has one owner and a due date in the future.
3. Every conversation goes into a comment on the bank, the same day.
4. Status changes only when something really happened.
5. Files go in the right folder with the naming from 4.3, and key files are attached to the bank.
6. Demos, pilots and onboarding are requested as development tasks, never only by chat.
7. Lost always gets a reason.
8. Nothing internal (drafts, internal price talks) goes into folders or tasks the client can see.

---

## 11. Setup status and what is left

| # | Item | State |
|---|---|---|
| 1 | Pipeline statuses (3.3), in order | Done 29 Oct |
| 2 | Country and bank type sub-projects (3.1) | Done 29 Oct |
| 3 | Folders 01-07 in Sales Folder Comply Solutions (4.1) | Done 29 Oct |
| 4 | Client portal with both projects and both folders | Already in place |
| 5 | Sales and Development groups | Already in place; check that Shahid M should be in the Development group |
| 6 | Sales folder shared with Everyone@Ultar Tech | Decide: keep or remove (4.4) |
| 7 | Custom fields (3.4) on the Sales project and all sub-projects | Done 29 Oct |
| 8 | A sample bank task filled completely, as the model to copy (use the example in 3.5) | To do |
| 9 | Import the client's bank list (3.6) | Client or sales |
| 10 | First Monday pipeline review | Client + sales |

---

## 12. Questions and answers

**A bank has offices in two countries. Where does it go?**
Under the country where the decision is made. Mention the other office in a comment.

**The client added a bank under the wrong bank type.**
Move the task to the right sub-project; status, comments and files move with it.

**We need a new status.**
Agree it in the Monday review first: statuses cannot be deleted later.

**A bank went quiet.**
After two follow-ups with no answer, set On hold with a due date in 1-3 months and a comment.

**Can the client see our development tasks?**
Yes: the Comply Solution project is shared with the portal, so keep internal-only discussion out of its comments.
