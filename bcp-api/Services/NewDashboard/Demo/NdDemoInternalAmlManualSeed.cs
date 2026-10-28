using Reguliq.Api.Services.LandingAi;

namespace Reguliq.Api.Services.NewDashboard.Demo;

/// <summary>
/// Curated Internal AML Manual 290626 sections from the official PDF headings and viewer pages.
/// Demo extract uses this extract-style preview text — never raw parse markdown
/// (no Landing anchor tags or Page | N footers). No Landing AI.
/// Printed footer "Page | N" matches Chrome #page=N (63 pages).
/// </summary>
public static class NdDemoInternalAmlManualSeed
{
    public const int TotalPages = 63;

    /// <summary>
    /// Official refs, viewer pages, and extract-style clause text (same kind of
    /// preview Landing would persist — not a full parse dump).
    /// </summary>
    public static IReadOnlyList<PolicyClause> Sections { get; } =
    [
        new("1", """
            1. Introduction. The international and regional impact of Anti-Money Laundering and Terrorist Financing (AML/CTF) concerns has contributed to paying more attention to certain requirements in order to avoid the spread of such crimes, as they directly and negatively affect the local and international economies.

            In order to mitigate the impact of ML/TF crimes on the economy of the United Arab Emirates, and to protect the Banking sector from being misused by criminals, DFSA have adopted reforms and initiatives that aim at protecting the Banking sector.
            """, 10),
        new("2", """
            2. Purpose. This document represents the AML/CTF Compliance Program, and the primary purpose of this document is to regulate the responsibilities of the bank by applying the appropriate rules and regulations, and establishing the required standards to prevent the bank, its customers, products, services and employees from facilitating ML/TF transactions and to protect the bank from operational, legal and reputational risks.

            The objectives of this document are represented in the following:
            a. Establishing the principle of "Compliance is everyone's responsibility" by defining the tasks, roles and responsibilities of all businesses and employees in the bank in regards to Anti-Money Laundering and Terrorist Financing and Sanctions.
            b. Defining the approach adopted by the bank in regards to the application of the risk-based approach and the methodology for assessing customer risks, products, services, channels and activities from the perspective of Combating ML/TF.
            c. Establishing a mechanism for the bank to comply with the resolutions issued by the UN Security Council relating to combating terrorist financing and the proliferation of weapons of mass destruction.
            d. Establishing a mechanism for screening all clients' names against names that have been classified as designated persons by Local Authorities, Interpol, and the United Nations.
            e. Establish a mechanism to monitor the international sanctions that are issued and the procedures for dealing with them.
            f. Setting a mechanism for the bank's adherence to the standards and recommendations of FATF.
            g. Developing a mechanism to apply KYC principles, and the customer identification process.
            h. Developing a mechanism to implement EDD and Due Diligence procedures.
            i. Dealing with clients that are classified as PEPs and their relatives and close associates.
            j. Defining the roles, tasks and responsibilities of the department in the DIFC.
            k. Determine the regulatory controls that should limit ML/TF transactions.
            l. AML and CT / PF Training Program.
            """, 11),
        new("3", "3. Scope. Unless otherwise noted, this document applies to DIFC which are licensed and/or supervised by DFSA.", 12),
        new("4", "4. Statement. Each employee is held responsible for complying with this document. All employees are responsible for being fully aware of the content of this document in its current form. The full responsibility lies with the Senior Management to ensure that the bank has effective AML/CTF measures.", 12),
        new("5", "5. References and Regulations. This document builds upon the provisions of Decree Federal Law No. 20 of 2018 on Anti-Money Laundering and Combating the Financing of Terrorism and Illegal Organizations, Cabinet Decision No. (10) of 2019, Cabinet Decision No. (74) of 2020, and DFSA AML Rules.", 12),
        new("6", "6. Roles and Responsibilities. In addition to the provisions outlined in the (AML/CTF) Law and Cabinet Decisions that have direct or indirect effects on the DIFC or its employees in a personal capacity.", 13),
        new("6.1", "6.1 Cooperation with Local Authorities: Compliance Department at the DIFC is the only authorized party to exchange information with the FIU.", 14),
        new("6.3", "6.3 Senior Management of DIFC: Senior Management of DIFC is responsible for the continuous implementation and monitoring of complying with the requirements of Anti-Money Laundering and Counter Terrorist Financing in DIFC.", 15),
        new("6.5", "6.5 Senior Executive Officer (SEO): Provide the necessary support to compliance department to ensure that it is effectively performing their duties and responsibilities.", 16),
        new("6.14", "6.14 Advisory and Risk Function: Developing the department’s policies and procedures based on regulatory requirements and updating them.", 21),
        new("6.15", "6.15 Sanctions Function: Review the sanction lists issued by the United Nations Security Council, OFAC, the European Union and the UK Treasury, in addition to local sanctions (issued by EOCN).", 21),
        new("7", "7. Implementation.", 25),
        new("7.1", "7.1 Know your Customer (KYC) Principle AML Rule 14.4.2: The bank has adopted the principle of know your customer that enables employees to know their customers and better understand their financial transactions.", 25),
        new("7.2", "7.2 Elements of \"Know Your Customer\" Principle. Determining the legal entity of the customer and then define the necessary regulatory requirements, documents and approvals to accept the relationship.", 25),
        new("7.4", "7.4 General Requirements Applicable for All Relationships. RMs must follow the below minimum procedures when accepting new relationships and/or processing transactions.", 27),
        new("7.7", "7.7 Financial Transactions: All appointed employees are required to complete all required information, such as the purpose, source of funds, beneficiary information, before executing the financial transaction.", 37),
        new("7.7-a", "Monitoring and Reporting. Upon receiving a suspicious transaction report, the department must act immediately to conduct the necessary investigations, review the transactions, request the necessary documents and information, and take the appropriate decision for the case.", 38),
        new("7.7-b", "Significant changes to the customer's KYC information and/or to the customer transaction profile (CTP) should be made to the recorded information in the systems/files and should not be held until the next customer information or CADD review cycle. At any time, the information update is performed, RMs, BMs and Sales Representatives must ensure that the account continues to be used for the purpose originally stated.", 39),
        new("7.7-c", "Staff Reporting (1st and 2nd Line of Defence). Each RM, BM, Sales Representative and other front-liners must report any suspicious transaction to DIFC Branch Compliance AML Rule 13.2.2.", 40),
        new("7.7-d", "DIFC Branch Compliance Reporting. BM and RMs are fully responsible to respond to DIFC Branch Compliance inquiries regarding certain transactions or customers where DIFC Branch Compliance has suspicion. From 27 June 2019, the FIU requires all Relevant Persons to register on the GoAML system. Suspicion activity or transaction detected: The Firm's Employee identifies that a Person is engaged in or has engaged in money laundering or terrorist financing. Information contained in an SAR is confidential.", 41),
        new("7.8", "7.8 Politically Exposed Person, and their Close Associates: Natural persons who are or have been entrusted with prominent public functions in the State or any other foreign country.", 43),
        new("7.9", "7.9 Sanctions, Prohibition of Dealing, and Alerting Bulletins issued by FATF / EOCN.", 43),
        new("7.11", "7.11 International and Local Payments. Complying with transparency standards and to ensure that local/international remittance messages contain complete and accurate information about the sender and beneficiary of the transfer.", 45),
        new("8", "8. Independent Reviews. Semi Annual Business Risk Assessment. DIFC Branch Compliance will conduct DIFC Branch Semi Annual Business AML risk assessment which will be approved by senior management.", 48),
        new("9", "9. Reviews and Updates. The Anti-Money Laundering and Terrorist Financing document is reviewed by the Head of the Department on a periodic basis or in the event of any changes in UAE regulations and Cabinet decisions.", 52),
        new("10", "10. AML Statement and Whistleblowing Unit. In case of suspecting money laundering or terrorist financing activities and Sanctions, then you are obliged to report this suspicion to the AML/CTF Division.", 52),
        new("13", "13. Appendices. Annex 1. Red Flag Indicators for TF and PF. Accurately identifying and assessing the TF and PF risks of a customer or business relationship is critical for appropriately managing these risks.", 54),
    ];
}
