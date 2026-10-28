# How Comply Solutions protects your documents

A plain explanation for security and compliance teams. Companion to [README.md](README.md) (the internal plan).
Items marked "available" exist today. Items marked "before your install" are built or confirmed during the project, and we say so
rather than promise them early.

## 1. The principle

Your documents are processed in an environment you control. The hosted demo portal is for sample, non-sensitive documents only. When
you proceed, Comply Solutions is installed on your servers or in your private cloud account. Your database, your file storage, your
keys, your network rules.

## 2. Your three choices

| Option | Where it runs | What may leave your environment |
|---|---|---|
| In-house with protected cloud services (most banks) | Your servers. Self-hosted open source Supabase (database, sign-in, files) | Only to services you approve: your own Azure Document Intelligence, and an AI model route you choose, within the region you choose |
| In-house, fully contained | Your servers | Nothing. Local document reading and a model you host |
| Hosted demo | Our cloud | Sample documents only |

## 3. What we send to AI, and what we do not

In a standard analysis, for each clause we send the clause text and the few policy passages found for it. We do not send user names or
account details. The answers come back and are stored in your database. There is one optional feature, a holistic "qualitative assessment" of a
policy, that sends much more of the text. It is switched off unless someone turns it on for a run, and we can disable it completely for your install.

## 4. The two cloud services, in detail

### Document reading: Azure Document Intelligence (optional)
- You may skip it: the product also reads PDFs and scans with engines that run entirely on your server (PdfPig, Tesseract, RapidOCR, Docling).
- If you use it, we use YOUR Azure resource in YOUR tenant and region, with private networking where you want it. Microsoft documents that analysis
  runs in the region of the resource, traffic is encrypted over HTTPS, and results are held temporarily in encrypted storage in that region and
  can only be fetched with the same key. Source: Microsoft Learn, "Data privacy and security for Azure AI Document Intelligence".
- Available now: choice of local engines or Azure. Before your install: confirm your tenant settings (region, private endpoint).

### AI analysis: your model route
- Option 1: your own Azure OpenAI deployment in your tenant (Microsoft's enterprise terms apply).
- Option 2: OpenRouter with strict data controls. OpenRouter supports per-request routing flags: `zdr: true` (only endpoints with a zero-data-retention
  policy) and `data_collection: "deny"` (exclude providers that store or train on data). Source: OpenRouter, "Zero Data Retention (ZDR)". Available: Comply Solutions
  sends both on every OpenRouter request by default, and shows what it sends on an admin screen. These rules limit which models can answer (we tested the whole list we offer);
  you approve the list of models.
- Option 3: a model you host yourself (fully contained option).
- In every option, we do not use your content to train any model, and we will not use a route that does not let us commit to that in writing.

## 5. Controls summary
- Separate workspaces, with the database filtering every row to its own workspace (available).
- Role-based access: admin, maker, checker, reviewer (available).
- Encryption in transit on every connection. Encryption at rest through your disk or database encryption (before your install: confirmed per environment).
- Stored cloud-storage sign-in tokens are encrypted by the application (available).
- Secrets held in your secret store (before your install).
- Network egress allow-list to only the approved endpoints: the application itself refuses calls to any host you have not listed (available), and your firewall enforces the same list (with your network team).
- Activity records: who imported, uploaded, reviewed and when (available, being extended).
- No standing access for our staff. Support only when you open a ticket, through your controls, recorded and ended afterwards.
- You choose retention. Deleting a workspace removes its documents, search vectors and results (before your install: tested and documented).
- Updates delivered as signed packages you apply in your own change window, with rollback (before your install).

## 6. Questions banks usually ask

**Will our documents be on the internet or on your servers?**
In an in-house install, no. They are in your database and storage. The only traffic that leaves is the traffic you approve in your firewall.

**Does the AI provider keep or learn from our documents?**
That depends on the route you pick. With your own Azure OpenAI, Microsoft's enterprise terms apply. With OpenRouter we enforce zero retention and no training on
every request and give you the list of models we can use under those rules. With a self-hosted model, nothing leaves.

**Can your staff read our documents?**
No standing access. Support is by your invitation, restricted, recorded and time-limited. We never copy documents out.

**Where is the data?**
Where you install it. For Azure services, in the region you pick for your resource. We can use a regional data centre close to you.

**Can we run it with no internet at all?**
Yes, in the fully contained option, using local document reading and a model you host. Expect to size hardware for the model and to validate the results
during the pilot.

**What happens when the contract ends?**
You keep your environment. We provide the steps and a checklist to delete the application data, and we do not hold a copy.

**How do you handle updates and vulnerabilities?**
Signed release packages, a release note for each, and a rollback plan. We tell you about security fixes first.

**What evidence can we review?**
Architecture and data-flow diagrams, the list of services and where data goes, a completed security questionnaire, and the pilot acceptance report.
(Before your install: the whitepaper and an independent penetration test report are being prepared.)

## 7. What we need from you to start
1. Which option (section 2).
2. Your approved AI route and the regions you allow.
3. Your hosting target and who manages the servers, network and certificates.
4. Your identity requirements (single sign-on).
5. Retention periods and your data classification.
6. A security contact for the questionnaire and the pilot.
