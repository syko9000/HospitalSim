# HospitalSim

A fake hospital in a fake town, generating realistic HL7v2 ADT traffic over MLLP — a standalone
generator that can point at any HL7 receiver.

## Projects

- `HospitalSim.World` — the world model (town, hospital, nursing units/beds, doctors, insurance
  companies, families, people) and a deterministic generator (`WorldGenerator`), persisted to
  `world.json` via `WorldStore`.
- `HospitalSim.Hl7` — minimal hand-rolled HL7v2 message building (`AdtMessageBuilder`, `AckBuilder`), a
  lenient parser (`Hl7ParsedMessage`) for reading inbound messages back apart, and MLLP over TCP in both
  directions (`MllpClient` for sending, `MllpListener` for receiving). This project only ever emits and
  parses its own well-formed text, dependency-free fit
  for a standalone repo.
- `HospitalSim.Registration` — the "registration system": on first run, generates and saves a world
  (a hospital with ~250 families / ~750 people, 18 doctors across specialties, 5 insurers, 6 nursing
  units). It then does two things concurrently:
  - loops forever, randomly admitting and discharging patients, sending an ADT^A01/A03 over MLLP for
    each event and tracking bed occupancy so it never double-books a bed. Every admit carries an OBX
    with LOINC 8661-1 (Chief Complaint) and a random reason for the visit (`ClinicalCatalog.ChiefComplaints`
    in `HospitalSim.World`), giving a future clinical system something to actually react to. It
    deliberately never originates a transfer itself - a bed move is a clinical decision, not something
    registration invents;
  - listens on its own MLLP port for inbound ADT^A02, so a clinical system can move a patient (e.g.
    after ordering a bed change) the way real hospitals' feeder systems talk back to registration.
    Unrecognized message types, an A02 for a patient who isn't currently admitted, or a target bed
    that's already occupied all get NAK'd (`AE`) rather than silently corrupting the census.
- `HospitalSim.Sink` — an MLLP black hole: accepts any number of concurrent connections, ACKs (`AA`)
  every message regardless of type or content, and stores/routes nothing. Point several of an engine's
  outbound interfaces (clinical, lab, rad, ...) at it when you need messages to actually flush out of
  a queue but don't have (or don't want) a real destination system standing behind each one.
- `HospitalSim.Clinicals` — the clinical system. Listens for ADT and tracks who's currently in, keyed by
  visit number (`VisitState`/`VisitStateStore`, persisted the same load-or-create way as Registration's
  world/census). ADT^A01 records someone in (including PV1-7, the attending doctor, kept as the ordering
  provider) and ADT^A03 records them out; anything else (an A02, a message missing PV1-19) is logged and
  skipped, not NAK'd - Clinicals only ever follows Registration's feed, it isn't a source of truth, so an
  A03 for a visit it never saw the A01 for (e.g. it started up after that admit happened) is expected,
  not an error. It stays in sync on its own as Registration's messages keep arriving; always ACKs `AA` so
  nothing it doesn't understand yet ever blocks the queue. It also *originates* traffic of its own: on a
  loop, it picks a random currently-known visit and a random test from `ClinicalCatalog.OrderableTests`
  (LAB/RAD/PATH) and sends an ORM^O01 - only ever for someone it currently believes is admitted, never
  invented. Unlike Registration's ADT broadcast, an order is point-to-point: MSH-5 names the specific
  department (`LAB`/`RAD`/`PATH`), which a `ClinicalsXL`-style translation routes on directly.

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

`docker-compose.yml` defines all three services. `registration` persists the generated world in a
named volume (`hospital-sim-data`, mounted at `/data`) so it survives restarts, and points
`HOSPITALSIM_MLLP_HOST` at `host.docker.internal` by default — change it to wherever the receiver
actually runs (a shared Docker network hostname, another host's address, etc). `sink` just listens on
`6670` with no configuration required. `clinicals` listens on `6680` and persists its visit state in
its own named volume (`clinicals-data`).

Registration's environment variables (all optional):

| Variable | Default | Purpose |
| --- | --- | --- |
| `HOSPITALSIM_WORLD_PATH` | `world.json` | where the generated world is persisted/loaded |
| `HOSPITALSIM_CENSUS_PATH` | `census.json` | where current admissions (who, where, visit number) are persisted/loaded |
| `HOSPITALSIM_MLLP_HOST` | `localhost` | HL7 receiver host |
| `HOSPITALSIM_MLLP_PORT` | `6661` | HL7 receiver port |
| `HOSPITALSIM_LISTEN_PORT` | `6660` | port Registration listens on for inbound ADT^A02 |
| `HOSPITALSIM_INTERVAL_SECONDS` | `5` | average seconds between ADT events |
| `HOSPITALSIM_SENDING_APP` / `HOSPITALSIM_SENDING_FACILITY` | `REGISTRATION` / `MRMC` | MSH-3/MSH-4, and Registration's own identity on ACKs it sends |

Registration's outbound ADT (A01/A03) is a broadcast, not addressed to one system, so MSH-5 is always
empty and MSH-6 is always the sending facility (it never leaves the hospital). ACKs to inbound messages
address themselves back to whoever actually sent that message (its own MSH-3/4), not a fixed value.

Sink's environment variables (all optional):

| Variable | Default | Purpose |
| --- | --- | --- |
| `SINK_LISTEN_PORT` | `6670` | port the sink listens on |
| `SINK_APP` / `SINK_FACILITY` | `SINK` / `SINK` | sending app/facility on the ACKs it returns |

Clinicals' environment variables (all optional):

| Variable | Default | Purpose |
| --- | --- | --- |
| `CLINICALS_LISTEN_PORT` | `6680` | port Clinicals listens on for ADT |
| `CLINICALS_STATE_PATH` | `state.json` | where the visit state is persisted/loaded |
| `CLINICALS_APP` / `CLINICALS_FACILITY` | `CLINICALS` / `MRMC` | MSH-3/MSH-4 on orders, and Clinicals' own identity on ACKs it sends |
| `CLINICALS_ORDER_MLLP_HOST` | `localhost` | order receiver host |
| `CLINICALS_ORDER_MLLP_PORT` | `6662` | order receiver port |
| `CLINICALS_ORDER_INTERVAL_SECONDS` | `20` | average seconds between orders |

## Later

Results for labs/path/rads (ORU traffic, generated by whatever eventually plays Lab/Rad/Path and sent
back toward Clinicals) is the next phase, not yet started. Design intent for when that happens:

- Done for Clinicals, same intent applies to whatever comes after it (Lab, Radiology, ...): a separate
  container per source system, each with its own MSH sending application and its own MLLP port —
  mirroring how a real hospital's interfaces work. No shared state file between them: state moves only
  through HL7 messages (e.g. the clinical system sending an A02 back to Registration to move a patient),
  same as production, hub-and-spoke through the receiver in between.
- Deliberately don't make every system's HL7 "correct" by the same standard. Real interface traffic is
  full of vendor drift — one system might put the patient ID in PID-2 instead of PID-3, or carry
  Registration-only data in a Z-segment that the clinical system expects migrated into a standard field.
  Baking a quirk or two like that into the clinical system's outbound format (rather than having every
  source system agree on one canonical layout) gives a translation step actual work to do, which is the
  point of feeding this into an interface engine in the first place.
- Done: Registration no longer originates bed moves itself (removed `TransferRandomPatientAsync` -
  admit/discharge only in the self-initiated loop). A transfer is a clinical decision now that there's a
  real inbound A02 path; still open once a clinical system exists to actually drive it: have the inbound
  A02 handler re-broadcast the now-confirmed move outbound (through `reg_in` → RegistrationXL → the
  fan-out) instead of only ACKing the sender and updating the local census silently.
- Registration and billing are tightly coupled in real hospitals: an ADT^A08 carrying DG1 (diagnosis)
  segments flows back into Registration (from billing/HIM, once that exists), and Registration echoes
  out its own ADT^A08 with everything it now knows. Not started - noted for whenever billing becomes a
  real piece of this.
