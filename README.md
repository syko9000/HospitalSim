# HospitalSim

A fake hospital in a fake town, generating realistic HL7v2 ADT traffic over MLLP — a generator that
can point at any HL7 receiver.

## Projects

- `HospitalSim.World` — the world model (town, hospital, nursing units/beds, doctors, insurance
  companies, families, people) and a deterministic generator (`WorldGenerator`), persisted to
  `world.json` via `WorldStore`.
- `HospitalSim.Hl7` — minimal hand-rolled HL7v2 message building (`AdtMessageBuilder`, `AckBuilder`), a
  lenient parser (`Hl7ParsedMessage`) for reading inbound messages back apart, and MLLP over TCP in both
  directions (`MllpClient` for sending, `MllpListener` for receiving). This project only ever emits and
  parses its own well-formed text, dependency-free.
- `HospitalSim.Registration` — the "registration system": on first run, generates and saves a world
  (a hospital with ~250 families / ~750 people, 18 doctors across specialties, 5 insurers, 6 nursing
  units). It then does three things concurrently:
  - an arrival loop, on an interval shaped by hour-of-day and day-of-week (`ArrivalRateMultiplier` -
    quiet overnight, busiest evening, a modest Friday/Saturday bump), rolls a random `ArrivalChannel`
    (`ED` / `ChildrensWard` / `FrontDesk` / `LaborAndDelivery`, relative likelihood set by
    `HOSPITALSIM_CHANNEL_WEIGHT_*`) and queues an eligible not-yet-in-system person for it as a
    `PendingArrival`, with a real wait span before anyone decides what happens to them - not persisted,
    since a restart mid-wait losing one pending arrival is a fine simplification for a demo tool. The
    channel is what someone actually is, not flavor text: `ChildrensWard` only takes under-18s and heads
    straight for PEDS, `LaborAndDelivery` only takes female patients in the configured childbearing age
    range (`HOSPITALSIM_CHILDBEARING_MIN_AGE`/`_MAX_AGE`) and only heads for L&D - there's no other way
    into either of those two units - `FrontDesk` (scheduled procedures/surgery) heads for MS3/MS4, and
    `ED` is `ED`. The wait scales with how full *that channel's own target unit(s)* currently are, never
    whole-hospital occupancy - an empty ICU doesn't get an ED patient seen faster, and a full one doesn't
    slow down a scheduled front-desk admission;
  - a disposition loop checks that queue for anyone whose wait is up and rolls their disposition within
    the channel's own target unit(s) only (`ChildrensWard`/`LaborAndDelivery` are always inpatient -
    nobody walks in there just to go home; `ED`/`FrontDesk` still roll the normal
    `HOSPITALSIM_INPATIENT_PROBABILITY` coin flip, falling back to outpatient if nothing in that
    channel's unit(s) is free) sends an ADT^A01, everyone else sends an ADT^A04 (registered, no bed -
    PV1-2 carries `O`, not `I`). Every admit/register carries an OBX with LOINC 8661-1 (Chief Complaint)
    and a random reason for the visit (`ClinicalCatalog.ChiefComplaints` in `HospitalSim.World`), giving
    a downstream clinical system something to actually react to. Registration deliberately never
    originates a transfer, discharge, or class change itself past this point - all three are clinical
    decisions;
  - listens on its own MLLP port for inbound ADT^A02 (transfer, inpatient only - an outpatient has no
    bed to move), ADT^A03 (discharge, either class), and ADT^A06 (outpatient -> inpatient class change -
    registration picks the actual bed here, unlike a transfer's specific requested target; unlike a
    fresh arrival's channel-scoped search, this checks the general `EligibleUnit` filter - PEDS<18,
    L&D=female - across every unit, since an A06 isn't tied to any particular channel), so a clinical
    system can drive all three the way real hospitals' feeder systems talk back to registration. PID-3.1
    is only trusted as the patient's MR when PID-3.4 (assigning authority) and PID-3.5 (identifier type
    code) actually qualify it as one - a
    bare, unqualified PID-3.1 gets NAK'd, same as an unrecognized message type, a patient in the wrong
    class for what's being asked, or a target bed that's already occupied. An accepted
    move/discharge/class-change is bounced back out to the broadcast feed, rebuilt from census/world
    state in registration's own message format - never a copy of the bytes that came in. A *rejected*
    transfer (target bed occupied) gets the same treatment in reverse: registration rebroadcasts the
    patient's actual current location right then, so Clinicals' picture of where they are self-corrects
    instead of drifting further from the census every time a guess misses.
- `HospitalSim.Sink` — an MLLP black hole: accepts any number of concurrent connections, ACKs (`AA`)
  every message regardless of type or content, and stores/routes nothing. Point several of an engine's
  outbound interfaces (clinical, lab, rad, ...) at it when you need messages to actually flush out of
  a queue but don't have (or don't want) a real destination system standing behind each one.
- `HospitalSim.Ancillary` — one program, three services: Lab, Rad, and Path are the same code
  (`ANCILLARY_DEPARTMENT` picks the identity/catalog subset), run as three separate containers rather
  than tripled as three separate projects, since their behavior only ever differs by department, not by
  logic. Accepts an ORM^O01 from Clinicals, samples a turnaround time from that specific test's range in
  `ClinicalCatalog` (minutes for a STAT lactate, 1-3 days for a culture or surgical pathology - a real
  clock per order, not a coin flip), and fires an ORU^R01 back when it's up. Also implements the reflex
  pattern: a small chance (`ClinicalCatalog.ReflexRules` - e.g. an abnormal-looking TSH reflexing to a
  Free T4) that finishing one result triggers this department to originate a follow-up order on its own,
  which the ordering clinician never asked for. See the placer/filler handshake below for how that gets
  a proper order number.
- `HospitalSim.Clinicals` — the clinical system, and the one that actually owns a visit's lifecycle.
  Listens for ADT and tracks who's currently in, keyed by visit number (`VisitState`/`VisitStateStore`,
  persisted the same load-or-create way as Registration's world/census). ADT^A01 records someone in as
  inpatient, ADT^A04 as outpatient (both keep PV1-7, the attending doctor, as an opaque string -
  Clinicals never parses it into components, just echoes it back out later), ADT^A02 updates which unit
  a tracked visit is currently in, ADT^A06 promotes a tracked visit to inpatient, and ADT^A03 records
  someone out; anything else (a message missing PV1-19, a transfer/class-change for a visit it isn't
  tracking) is logged and skipped, not NAK'd - Clinicals only ever follows Registration's feed, it isn't
  a source of truth, so gaps are expected, not errors. It stays in sync on its own as Registration's
  messages keep arriving; always ACKs `AA` so nothing it doesn't understand yet ever blocks the queue.
  ADT^A02/A04/A06/A07 tell it who's currently tracked; ORU^R01 and ORM^O01 (see below) are the other two
  message types it now understands inbound, alongside ADT. It also *originates* traffic of its own, on
  two independent loops:
  - an order loop picks a visit (outpatients weighted `CLINICALS_OUTPATIENT_ORDER_WEIGHT`x heavier than
    inpatients - a short outpatient stay is test-heavy, an inpatient stay gets sparse routine checks
    only). Inpatient: `ClinicalCatalog.RoutineTests`, sent as an ORM^O01. Outpatient: usually a
    full-catalog `OrderableTests` pick, also an ORM^O01, but sometimes (`CLINICALS_PROCEDURE_CHANCE`) a
    `ClinicalCatalog.Procedures` entry instead - and a procedure is performed by Clinicals itself, not
    ordered out to a separate department the way a lab/rad/path test is, so nothing goes out on the wire
    for it at all (only the result would, once that's modeled - not built yet). Unlike Registration's ADT
    broadcast, an order is point-to-point: each department (Lab/Rad/Path) is its own destination now
    (`CLINICALS_LAB_MLLP_HOST` etc - Clinicals routes per department itself, playing the role an
    interface engine's MSH-5 routing would elsewhere), and MSH-5 still names the department so a
    downstream translation has something to route on too. PID-3 on an order is bare - just the
    MR in PID-3.1, no assigning authority or identifier type - since nothing downstream of an order needs
    more than that;
  - a lifecycle loop drives everything else off each visit's own clock, not a per-tick coin flip: at
    record-in time (A01/A04/A06) a length of stay is sampled once (`CLINICALS_INPATIENT_LOS_*_HOURS` /
    `CLINICALS_OUTPATIENT_LOS_*_HOURS`) and an ADT^A03 fires when it's up - a patient can't get
    discharged moments after being admitted. Separately, each tick: only an ED or ICU boarder is a
    transfer candidate (`CLINICALS_TRANSFER_CHANCE_PER_CHECK`, weighted `CLINICALS_ED_TRANSFER_WEIGHT`x
    toward ED over ICU) - nobody already on a ward has a further step-down target defined. The target
    itself follows a real rule (`StepDownTarget`), not a flat random pick: from the ED, ICU or the
    age-appropriate ward (PEDS if the visit's DOB says under-18, otherwise MS3/MS4); from the ICU,
    always straight to the age-appropriate ward, never back to the ED. An outpatient may instead get
    promoted with an ADT^A06 (`CLINICALS_A06_CHANCE_PER_CHECK`) rather than ever discharging - "a small
    chance of admitting them" - and, the mirror of that, an inpatient may get stepped back down with an
    ADT^A07 (`CLINICALS_A07_CHANCE_PER_CHECK`), freeing their bed on registration's side without a full
    discharge. Clinicals has no view of registration's actual bed layout (separate process, separate
    state file), so a transfer's specific room/bed guess is still one registration is free to reject
    even when the unit itself is the right call; an A06 proposes no location at all, since bed placement
    there is registration's call, same as a fresh admit. Every one of these checks the ACK it gets back
    (MSA-1) rather than assuming success - a NAK'd transfer/discharge/escalation/demotion logs distinctly
    (`[TRANSFER-REJECTED]` etc.) instead of being written off as if it happened. Treating any ACK as a
    success is exactly how Clinicals' picture of the world would silently drift from registration's
    actual census over time. DOB is the one demographic Clinicals actually keeps (parsed from PID-7,
    which registration's broadcast already carries) - it's what step-down routing above is age-aware
    from. Everything else Clinicals never captured (sex, address, SSN, insurance) go out blank/unknown
    rather than
    invented; IN1 is omitted entirely since Clinicals never tracked insurance at all.

### Order results and the reflex handshake

An ORM^O01 carries a placer order number (ORC-2/OBR-2, the ordering system's number) and a filler order
number (ORC-3/OBR-3, the fulfilling system's number) independently - each side only ever assigns its
own:

- **A normal order**: Clinicals places it, so it sets the placer number; the filler number is blank
  until Lab/Rad/Path accepts the order and assigns its own.
- **A reflex order**: the department decides on its own to run a follow-up test (see
  `ClinicalCatalog.ReflexRules`) - it isn't the placer, so it sends the new ORM^O01 with the placer
  number blank and its own filler number set, ORC-1 `NW`.
- **The placer-number update**: Clinicals receives that reflex order, assigns it a placer number (same
  numbering scheme as any other order), and replies with an ORM^O01 of its own, ORC-1 `XO` (change,
  not new), carrying both numbers. The department correlates that back to its own pending order by the
  filler number it already assigned, and files the now-known placer number - the record has to already
  exist by the time that reply arrives (it's recorded before the reflex order is even sent, not after),
  since the reply can come back fast enough to race a later write.
- **The result**: an ORU^R01 carries both numbers back, whichever order (normal or reflex) it's
  reporting on. Result content is a plain canned string (`Within normal limits` / `Abnormal - see
  report`, ~15% abnormal) - this isn't modeling real reference ranges or numeric values, just enough to
  look like a result landed.

## Running

Directly with the .NET SDK:

```bash
cd src/HospitalSim.Registration   # or src/HospitalSim.Sink
dotnet run
```

In Docker:

```bash
docker compose up --build
```

`docker-compose.yml` defines all six services. `registration` persists the generated world in a named
volume (`hospital-sim-data`, mounted at `/data`) so it survives restarts, and points
`HOSPITALSIM_MLLP_HOST` at `host.docker.internal` by default — change it to wherever the receiver
actually runs (a shared Docker network hostname, another host's address, etc). `sink` just listens on
`6670` with no configuration required. `clinicals` listens on `6680`, persists its visit state in its
own named volume (`clinicals-data`), and routes orders directly to `lab`/`rad`/`path` by compose service
name - no `host.docker.internal` needed there, they're all on the same network. `lab`/`rad`/`path` each
run the same `HospitalSim.Ancillary` image (`ANCILLARY_DEPARTMENT` set per service), each with its own
named volume (`lab-data`/`rad-data`/`path-data`) and host port (`6690`/`6691`/`6692`).

Registration's environment variables (all optional):

| Variable | Default | Purpose |
| --- | --- | --- |
| `HOSPITALSIM_WORLD_PATH` | `world.json` | where the generated world is persisted/loaded |
| `HOSPITALSIM_CENSUS_PATH` | `census.json` | where current admissions (who, where, visit number, class) are persisted/loaded |
| `HOSPITALSIM_MLLP_HOST` | `localhost` | HL7 receiver host |
| `HOSPITALSIM_MLLP_PORT` | `6661` | HL7 receiver port |
| `HOSPITALSIM_LISTEN_PORT` | `6660` | port Registration listens on for inbound ADT^A02/A03/A06/A07 |
| `HOSPITALSIM_INTERVAL_SECONDS` | `300` | average seconds between new arrivals at baseline (day/night and weekday shaped from there) |
| `HOSPITALSIM_WAIT_MIN_MINUTES` / `HOSPITALSIM_WAIT_MAX_MINUTES` | `5` / `60` | how long an arrival waits before disposition is decided |
| `HOSPITALSIM_INPATIENT_PROBABILITY` | `0.3` | chance an ED/FrontDesk disposition is decided inpatient rather than outpatient (falls back to outpatient anyway if nothing in that channel's unit(s) is free) |
| `HOSPITALSIM_DISPOSITION_CHECK_SECONDS` | `20` | how often the pending-arrivals queue is checked for anyone whose wait is up |
| `HOSPITALSIM_CHANNEL_WEIGHT_ED` / `_CHILDRENS_WARD` / `_FRONT_DESK` / `_LABOR_AND_DELIVERY` | `60` / `10` / `25` / `5` | relative likelihood of each arrival channel (normalized against each other, not absolute percentages) |
| `HOSPITALSIM_CHILDBEARING_MIN_AGE` / `_MAX_AGE` | `14` / `50` | age range eligible for the `LaborAndDelivery` arrival channel |
| `HOSPITALSIM_SENDING_APP` / `HOSPITALSIM_SENDING_FACILITY` | `REGISTRATION` / `WRMC` | MSH-3/MSH-4, and Registration's own identity on ACKs it sends |

The 300s/5-60min defaults are a starting guess for a long-running instance, not a tuned steady state -
with ~120 beds total and real (not compressed) length-of-stay, tune `HOSPITALSIM_INTERVAL_SECONDS` and
`HOSPITALSIM_INPATIENT_PROBABILITY` against however busy you actually want the place to look.

Registration's outbound ADT (self-initiated A01/A04, plus A02/A03/A06/A07 bounced back out after a
clinical-initiated move/discharge/class-change is accepted) is a broadcast, not addressed to one system,
so MSH-5 is always empty and MSH-6 is always the sending facility (it never leaves the hospital). ACKs to
inbound messages address themselves back to whoever actually sent that message (its own MSH-3/4), not a
fixed value.

Sink's environment variables (all optional):

| Variable | Default | Purpose |
| --- | --- | --- |
| `SINK_LISTEN_PORT` | `6670` | port the sink listens on |
| `SINK_APP` / `SINK_FACILITY` | `SINK` / `SINK` | sending app/facility on the ACKs it returns |

Clinicals' environment variables (all optional):

| Variable | Default | Purpose |
| --- | --- | --- |
| `CLINICALS_LISTEN_PORT` | `6680` | port Clinicals listens on for ADT, results (ORU^R01), and reflex orders (ORM^O01) |
| `CLINICALS_STATE_PATH` | `state.json` | where the visit state is persisted/loaded |
| `CLINICALS_APP` / `CLINICALS_FACILITY` | `CLINICALS` / `WRMC` | MSH-3/MSH-4 on orders, and Clinicals' own identity on ACKs it sends |
| `CLINICALS_LAB_MLLP_HOST` / `CLINICALS_LAB_MLLP_PORT` | `localhost` / `6662` | Lab's order-intake host/port |
| `CLINICALS_RAD_MLLP_HOST` / `CLINICALS_RAD_MLLP_PORT` | `localhost` / `6663` | Rad's order-intake host/port |
| `CLINICALS_PATH_MLLP_HOST` / `CLINICALS_PATH_MLLP_PORT` | `localhost` / `6664` | Path's order-intake host/port |
| `CLINICALS_ORDER_INTERVAL_SECONDS` | `20` | average seconds between orders |
| `CLINICALS_OUTPATIENT_ORDER_WEIGHT` | `3` | how much more likely an outpatient visit is picked for an order than an inpatient one |
| `CLINICALS_PROCEDURE_CHANCE` | `0.25` | chance an outpatient visit's turn is a performed-in-house procedure (no outbound order) instead of a catalog test |
| `CLINICALS_ADT_MLLP_HOST` | `localhost` | registration's inbound ADT listener host |
| `CLINICALS_ADT_MLLP_PORT` | `6660` | registration's inbound ADT listener port |
| `CLINICALS_ADT_INTERVAL_SECONDS` | `30` | average seconds between lifecycle checks (discharge-due scan, transfer/class-change rolls) |
| `CLINICALS_OUTPATIENT_LOS_MIN_HOURS` / `_MAX_HOURS` | `1` / `6` | sampled outpatient length-of-stay range |
| `CLINICALS_INPATIENT_LOS_MIN_HOURS` / `_MAX_HOURS` | `24` / `72` | sampled inpatient length-of-stay range |
| `CLINICALS_TRANSFER_CHANCE_PER_CHECK` | `0.08` | chance an eligible inpatient gets an A02 offered on a given lifecycle check |
| `CLINICALS_ED_TRANSFER_WEIGHT` | `3` | how much more likely a currently-ED inpatient is picked for that transfer than one already on a floor |
| `CLINICALS_A06_CHANCE_PER_CHECK` | `0.03` | chance an eligible outpatient gets promoted (A06) instead of eventually discharging |
| `CLINICALS_A07_CHANCE_PER_CHECK` | `0.05` | chance an eligible inpatient gets stepped down (A07) instead of eventually discharging |

Same caveat as Registration's arrival pacing: the LOS ranges are real hours, not compressed, so the
defaults above are a starting guess for what a long-running instance should look like, not a tuned one.

Lab/Rad/Path's environment variables (all optional - same variable names for all three, `ANCILLARY_DEPARTMENT` is what actually differentiates them):

| Variable | Default | Purpose |
| --- | --- | --- |
| `ANCILLARY_DEPARTMENT` | `LAB` | `LAB` / `RAD` / `PATH` - identity, MSH-3 default, and catalog turnaround lookups |
| `ANCILLARY_APP` | (department) | MSH-3 override, and this department's own identity on ACKs it sends |
| `ANCILLARY_FACILITY` | `WRMC` | MSH-4 |
| `ANCILLARY_CLINICALS_APP` | `CLINICALS` | who results/reflex orders are addressed to (MSH-5 on the way out, and the department-routing key Clinicals' own inbound ORM-XO handler looks up by MSH-3 on the way back) |
| `ANCILLARY_LISTEN_PORT` | `6690` | port this department listens on for inbound orders (ORM^O01, `NW` new or `XO` placer-number update) |
| `ANCILLARY_STATE_PATH` | `state.json` | where in-progress orders (`PendingResultState`) are persisted/loaded |
| `ANCILLARY_RESULT_MLLP_HOST` / `ANCILLARY_RESULT_MLLP_PORT` | `localhost` / `6680` | Clinicals' inbound listener - where results and reflex orders go |
| `ANCILLARY_CHECK_INTERVAL_SECONDS` | `30` | average seconds between checks for due results |

Every accepted order gets its own sampled turnaround (`ClinicalCatalog.OrderableTests`'
min/max-turnaround-minutes for that specific test - not a per-department flat range), same "real clock,
not a coin flip" approach as Clinicals' own length-of-stay.

## Design notes

Each source system's HL7 isn't "correct" by the same standard on purpose. Real interface traffic is
full of vendor drift — one system might put the patient ID in PID-2 instead of PID-3, or carry
Registration-only data in a Z-segment that the clinical system expects migrated into a standard field.
Baking a quirk or two like that into the clinical system's outbound format (rather than having every
source system agree on one canonical layout) gives a translation step actual work to do, which is the
point of feeding this into an interface engine in the first place.

## Roadmap

General directions, roughly nearest-to-farthest-out (specific tracked items live in a private project
board, not here):

- **Context-aware ordering** — tie what gets ordered to the patient's actual reason for being there and
  where they currently are, instead of a uniform random pick across the whole catalog.
- **More order types** — broaden beyond the current lab/rad/path/procedure set.
- **More ADT event types** — round out the trigger events beyond what's modeled today.
- **More expressive ADT** — NK1 (next of kin), GT1 (guarantor), ACC (accident info), and similar
  segments this simulator doesn't send yet.
- **Diagnoses and procedures** — DG1 (ICD-10 diagnoses) and PR1 (procedures actually performed, as
  opposed to just what got ordered) riding an ADT^A08 sent once coding's done, not just a
  chief-complaint string.
- **DRG** — diagnosis-related group assignment computed from that DG1/PR1 pair, the way billing/casemix
  would actually see a stay.
- **Further out** — X12 claims out to a payer and remits (835) coming back, closing the loop past HL7
  entirely.
