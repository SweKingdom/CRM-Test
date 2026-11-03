# Synthetic Dynamics-shaped export (US-184)

Writes deliberately messy, fake Dynamics-shaped CSV files so the import (US-185 and US-186) can be proven without real data. One Node file, no dependencies, no network.

```
node tools/fake-dynamics-export/generate.mjs --seed 42 --out ./out [--scale 1]
npm run fake-dynamics-export -- --seed 42 --out ./out
```

| Option | Default | Meaning |
|---|---|---|
| `--seed` | 42 | Whole number. The same seed and scale give byte-identical files. |
| `--out` | `./out` | Output folder (created if missing; `out/` is gitignored). |
| `--scale` | 1 | Multiplies every volume and mess count. Use `0.1` for a fast run. |

Reference "today" is fixed at 2026-10-31, so the output does not change from day to day. Scale 1 runs in a few seconds.

## Output

One UTF-8 CSV per table (header row, RFC 4180 quoting, `\n` line ends) plus `manifest.json` (seed, scale, rows per file, mess counts; no timestamps). The same numbers are printed on stdout.

| File | Rows (scale 1) | Columns |
|---|---|---|
| `account.csv` | 3,442 (incl. duplicates) | accountid, name, sirocco_countrychoice, address1_city, telephone1, emailaddress1, statecode, createdon, modifiedon |
| `contact.csv` | 8,323 | contactid, firstname, lastname, emailaddress1, telephone1, jobtitle, parentcustomerid, createdon |
| `lead.csv` | 6,000 | leadid, firstname, lastname, companyname, emailaddress1, leadsourcecode, statuscode, createdon |
| `opportunity.csv` | 1,195 | opportunityid, name, parentaccountid, estimatedvalue, stepname, estimatedclosedate, statecode, createdon |
| `project.csv` | 385 | projectid, name, sirocco_accountid, sirocco_contractingcountry, startdate, enddate, statecode |
| `timeentry.csv` | 105,000 | timeentryid, sirocco_projectid, sirocco_consultant, sirocco_workdate, sirocco_hours, sirocco_timetype |
| `activity.csv` | 36,000 | activityid, activitytypecode, subject, regardingobjectid, scheduledstart, statecode |
| `skill.csv` | 60 | skillid, name |
| `userskill.csv` | 595 | userskillid, skillid, sirocco_consultant, sirocco_level |

Total 161,000 rows. Leads and activities are dated over the 24 months before the reference date.

## Mess (counts at scale 1, reported in `manifest.json`)

| Key | Count | What |
|---|---|---|
| `duplicateAccounts` | 69 | Account rows that repeat an earlier GUID with a different name case, phone and city |
| `blankCountry` | 1,825 | Account rows with empty `sirocco_countrychoice` (53%) |
| `countryTypos` | 150 | Country values from `Sverige, Swedn, Norwey, Danmark, Finlnad, Itally, U.S.A.` |
| `outsideSixUnits` | 78 | Country is a clean name outside the six units (Italy, United States, France, Spain, Netherlands) |
| `inactiveAccounts` | 400 | Distinct account GUIDs last changed before 2023-10-31 and never referenced by an activity, opportunity or project |
| `blankLeadSource` | 600 | Leads with empty `leadsourcecode` (10%) |
| `messyStages` | 420 | Opportunities whose `stepname` is not exactly `Qualify`, `Develop`, `Propose` or `Close` (for example `qualify`, `Qualification`, `Closed won`) |
| `orphanContacts` | 40 | Contacts whose `parentcustomerid` matches no account |
| `orphanOpportunities` | 12 | Opportunities whose `parentaccountid` matches no account |
| `orphanTimeEntries` | 105 | Time entries whose `sirocco_projectid` matches no project |

Every other reference holds. All shares are constants at the top of `generate.mjs`; counts are tallied from the rows written, so the manifest is exact.

## Fake data

Every person, company, city and skill name comes from `names.json` (companies read like `Lindholm Demo AB`); emails end in `@demo.example`. Nothing is read from a real system.

## Assumptions to confirm

- `docs/data/f21-field-mapping-spec.md` is not in the repository yet. Table and column names here follow the story text (for example `sirocco_countrychoice`); compare them with the spec when it lands and record any mismatch there.
- The six units are assumed to be Sweden, Norway, Denmark, Finland, Germany and United Kingdom.
- The split of the ≈161,000 rows (6,000 leads, 36,000 activities, 60 skills, 595 user skills) is chosen to match the stated total.
