# WebPos

A simple point-of-sale web app: **ASP.NET Core / Blazor (Interactive Server)**, **Radzen Blazor** components,
**EF Core** on **PostgreSQL**, with fixed-width reports sent raw to a dot-matrix line printer such as the
**OKI Microline 4410**.

## Features

- **Login** (ASP.NET Core Identity) with a user name and password or by scanning an **ID card**. Every user has one
  role: Cashier, Manager or SuperAdmin. See [Users, roles and logging in](#users-roles-and-logging-in).
- **Checkout** (`/`): scan barcodes (USB keyboard-wedge scanners work out of the box), type `3*<barcode>` for a
  quantity, or click items in the searchable grid. Take payment by cash (with change calculation and quick-tender
  buttons), card, or other. The signed-in user is recorded as the cashier. Click a price to change it.
  Removing items, clearing the sale and changing prices need a manager, who can approve at the register
  without the cashier logging out.
- **Clock in / out** from the header. You have to be on the clock to take payment, and each stretch is saved as a
  shift for the *Time cards* report.
- **Receipts:** after each sale a 40-column till receipt appears under the cart, ending in the receipt number as a
  barcode (`R000042`). *Print receipt* opens it on its own page for the browser's print dialog. Any sale's receipt
  can be shown again from its transaction.
- **Customer mode** (switch on the checkout page): a practice game. See [Customer mode](#customer-mode).
- **Barcode sheet** (`/items/barcodes`): every item's barcode, to scan off the screen or print.
- **Transactions** (`/transactions`): browse sales by date and open one to see its lines and receipt. Scan or type a
  receipt number to jump straight to that sale. Voiding needs a manager.
- **Reports** (`/reports`, managers): sales summary (totals, by payment method, by cashier, by hour or by day),
  item sales, transaction journal, price list, and time cards (shifts and hours per person). You can preview them on
  screen, print them, or download them as `.txt`.
- **Items** (`/items`, managers): inline-editable list. Items are deactivated, not deleted, so they keep their sales history.
- **Users** (`/users`, superadmins): add users, set their role, password and ID card, and deactivate them.

## Running it

Requirements: .NET 10 SDK. Docker is only needed for the tests and the local sandbox database.

```bash
dotnet run
```

Run it from the repository root, which holds the web project (`WebPos.csproj`); the tests are in `tests/WebPos.Tests`.
Open http://localhost:5263 and log in as **`superadmin`** / **`superadmin12345`**.

### Database

The app uses the Postgres server at **10.0.150.130** (database `webpos`, user and password `webpos`), configured in
`ConnectionStrings:Pos` in `appsettings.json`. On startup it creates the database if it is missing and
applies any pending migrations. For deployments, override the setting with the environment variable
`ConnectionStrings__Pos` instead of keeping the password in the file.

**Sample data.** `Data/SampleDataSeeder.cs` holds a starter catalog of 15 convenience-store items with typical
US prices (water $1.29, milk $4.29, batteries $6.99, …). Their barcodes are in-store UPC-A codes starting with `4`
(e.g. `400000001104`), and bananas use the produce code `4011`. There are also three demo users with known
passwords. Two settings, both off by default, add them at startup:

| Setting | Adds |
|---|---|
| `Database:SeedSampleItems` | the 15 sample items, if the item table is empty |
| `Database:SeedSampleUsers` | demo users, each only if its user name is free (below) |

| User | Role | Password | ID card code |
|---|---|---|---|
| `alice` | Cashier | `cashier123` | `100000001` |
| `bob` | Cashier | `cashier123` | `100000002` |
| `maria` | Manager | `manager123` | `200000001` |

Never turn on `SeedSampleUsers` for a real store. The real database was seeded with the items only. Add your staff
on the *Users* page.

For a throwaway local copy with all the demo data, use the Docker database and the `local-docker` launch profile,
which turns both settings on. Type a card code into the ID card box and press Enter to log in as if by card:

```bash
docker compose up -d
```
```bash
dotnet run --launch-profile local-docker
```

### Tests

```bash
docker compose up -d
```
```bash
dotnet test tests/WebPos.Tests
```

The tests create and drop a temporary `webpos_test_*` database on the local Docker Postgres (override the server with
`WEBPOS_TEST_CONNECTION`, without a `Database=` part). Never point them at the real server.

### Migrations

```bash
dotnet tool restore
dotnet ef migrations add <Name> --output-dir Data/Migrations
```

## Users, roles and logging in

Users and roles are stored with ASP.NET Core Identity in the same Postgres database (`AspNetUsers`, `AspNetRoles`, …).

| Role | Can |
|---|---|
| **Cashier** | Checkout (scan, add, take payment), clock in and out, customer mode, receipts, the barcode sheet, the transaction list |
| **Manager** | everything a cashier can, plus remove items from a sale, clear a sale, change prices, void sales, reports, items, and approving those actions for cashiers |
| **SuperAdmin** | everything, plus the *Users* page |

- **Pages and menu:** the menu only shows what the role allows. Opening a page anyway (or a report `.txt` link)
  shows *No access*. The server checks again before it records a sale, void or price change, so a demoted or
  deactivated user can't do any of these, even with a page that was already open.
- **Manager approval:** when a cashier removes an item, clears the sale, changes a price or voids a sale, a dialog
  asks a manager to scan their ID card or type their user name and password. The cashier stays signed in and keeps
  the sale. A signed-in manager isn't asked. Approved price changes are saved on the sale line with the regular
  price and the manager's name. Voids record the manager's name too, and both appear in the transaction journal.
  An approved price applies to the quantity on that line: scanning the item again starts a new line at the regular price.
- **ID cards:** any card a keyboard-wedge reader can read (barcode, magstripe or RFID badge) that types a code of at
  least 6 characters followed by Enter. Assign one on the *Users* page by clicking in the ID card box and scanning the card.
  The login page has the card box focused, so a user can just scan. Only a SHA-256 hash of the code is stored,
  each card belongs to one user, and *Remove the current ID card* stops a lost card from working. Use cards with long
  numbers: a card works like a password that can't be locked out, so the login form is rate-limited instead
  (10 attempts per minute per machine).
- **Users page:** new users get a temporary password and are asked to change it at first login (untick the box to
  skip that). Users are deactivated rather than deleted, so their sales keep their name. Setting a new password also
  unlocks a locked-out account. You can't demote or deactivate yourself, or the last active superadmin.
  Changing someone's role, deactivating them or changing their card signs them out of their open sessions within 30 minutes.

- **Default account:** on startup, if the user table is empty, WebPos creates `superadmin` / `superadmin12345` in the
  `SuperAdmin` role. It is only created when there are no users, so it never resets a changed password or comes back after
  being removed. Override the defaults with the `DefaultAdmin:UserName` and `DefaultAdmin:Password` settings
  (e.g. environment variables `DefaultAdmin__Password`) before the first start.
- **Change the default password:** after logging in with it you are taken to *Change password*, and a banner stays on
  every page until you do. You can always reach the page from the link in the header.
- **Password rules:** at least 8 characters with a lowercase letter and a digit. This is relaxed from Identity's
  defaults, which would also require an uppercase letter and a symbol and so would reject `superadmin12345`.
  Change it in `Services/IdentitySetup.cs`.
- **Lockout:** 5 wrong passwords lock the account for 5 minutes.
- **Sessions:** the sign-in cookie lasts 12 hours and renews while in use. Open pages check every 30 minutes that the
  account is still valid, so changing a password ends sessions elsewhere.
- The login and change-password pages are plain server-rendered forms, because a sign-in cookie can't be set over
  Blazor's interactive connection. Everything else stays interactive.

## Customer mode

Switch on *Customer mode* at the checkout and made-up customers come to your till, one at a time, with a basket of
real items from the catalog. Each item shows its barcode on screen, so you can scan it with a USB scanner.

- **Ring them up:** scan everything (`3*` before a scan counts three), then take payment the way they want. For a
  card customer, choose *Card*. For a cash customer, enter what they hand you: the quick buttons or type it.
  Basket cards turn green when the right number is scanned and red if you scan too many.
- **Score:** 100 points for a perfect sale plus a speed bonus (2 points per second under par, which is 5 seconds
  per item plus 15 seconds to pay). Each mistake costs 25 points and is listed, e.g.
  *Customer handed you $20.00, you entered $9.18*. The stats line keeps your total, perfect streak and average time.
- **Next customer:** press Enter in the empty scan box (or click *Next customer*).
- **Twists** (on by default):
  - a smudged barcode you have to type in
  - produce with a PLU code such as `4011`, also typed in
  - a coupon, applied by clicking the item's price and changing it
  - a customer who puts something back once everything is scanned
  
  As a cashier, the coupon and the put-back need a manager's approval, as at a real till.
- **Practice sales are real sales.** They go into the transaction list and every report like any other sale.

**Scanners and screens.** Imager (camera) and CCD scanners read barcodes off a monitor; many laser scanners can't.
If yours won't, print the barcode sheet. Barcodes are drawn with every bar an exact number of pixels, so they are
sharpest when browser zoom times the system's display scaling is 100% (e.g. 80% zoom on a Windows screen set to 125%). Scanners that send a UPC-A code as 13 digits with a leading
`0` (or drop it) still find the item.

## Data model

| Table | Columns |
|---|---|
| `Items` | Name, Barcode (unique, optional), Category, Price, IsActive, CreatedAt, UpdatedAt |
| `AspNetUsers` | Identity's columns plus DisplayName, IsActive, MustChangePassword, CardHash (unique, optional). The role is in `AspNetUserRoles` |
| `Transactions` | Id (= receipt no.), CreatedAt (UTC), CashierId (→ user), **CashierName snapshot**, PaymentMethod, Subtotal, Tax, Total, AmountTendered, ChangeDue, Status (Completed/Voided), VoidedAt, VoidedBy |
| `TransactionItems` | TransactionId, ItemId (nullable), **Name, Barcode and UnitPrice snapshotted at sale time**, CatalogPrice and PriceOverriddenBy (set when a manager changed the price), Quantity, LineTotal |
| `Shifts` | UserId, ClockIn, ClockOut (UTC; null while on the clock, at most one open shift per user) |

Money is stored as `numeric(12,2)`. When a sale is completed, the server reads prices from the database again,
so changing an item's price later never changes past sales.

## Configuration (`appsettings.json`)

```jsonc
"ConnectionStrings": { "Pos": "Host=…;Database=webpos;Username=…;Password=…" },
"Store": {
  "Name": "WebPos Store",
  "TaxRate": 0,               // e.g. 0.0825 for 8.25 %, applied to the whole subtotal
  "Culture": "en-US",         // money/date formatting in the UI
  "TimeZone": ""              // e.g. "America/Chicago"; defines the business day for reports. Empty = server time
},
"Printer": { … }              // see below
```

The `local-docker` launch profile points `ConnectionStrings:Pos` at the Docker database instead. In production,
set it with the environment variable `ConnectionStrings__Pos`.

## Printing to the Microline 4410

Browser printing sends a dot-matrix printer raster graphics, which is slow and looks poor. Instead, WebPos
renders every report as **plain 7-bit ASCII**: fixed-width columns, CR LF line endings, a header with the page
number on every page, column headings repeated after each page break, and a **form feed** between pages. The
server sends this **raw** to the printer, which prints it in its fast built-in fonts. Accented characters are
folded (`é` becomes `e`, `€` becomes `EUR`) and control characters are stripped, so item names cannot inject
printer escape codes.

The ML4410 has a 136-column carriage at 10 cpi, emulates Epson FX, IBM Proprinter and OKI MICROLINE, and has
parallel and serial ports, with network cards as an option.

```jsonc
"Printer": {
  "Name": "OKI Microline 4410",
  "Transport": "File",        // File | Device | RawTcp | Cups
  "Path": "printouts",        // File: output folder (one .prn per job). Device: e.g. /dev/usb/lp0, /dev/ttyS0
  "Host": "", "Port": 9100,   // RawTcp: printer network card or print server (JetDirect-style port 9100)
  "Queue": "",                // Cups: queue name; jobs are submitted with `lp -d <queue> -o raw`
  "TimeoutSeconds": 15,
  "Width": 136,               // characters per line: 136 for 14-7/8" paper at 10 cpi, 80 for 8-1/2"
  "LinesPerPage": 66,         // form length in lines: 66 = 11" at 6 lpi
  "BottomMargin": 3,          // lines left blank above the perforation
  "FormFeedAtEnd": true,      // eject to the next top-of-form when done
  "InitSequence": "1B40",     // hex bytes sent first; ESC @ = reset (Epson FX). Use "" for IBM Proprinter emulation
  "Condensed": false          // send SI (17 cpi); raise Width to match (e.g. 233 on 14-7/8" paper)
}
```

Printer checklist:

1. Set the printer's **form length** to match `LinesPerPage` (11" / 66 lines is typical) and set **top of form**
   at the perforation, so each form feed lands on the next page.
2. Choose a transport:
   - **Network card / print server** → `RawTcp` with `Host` set to its IP.
   - **Parallel / USB-parallel on the server** → `Device` with `/dev/usb/lp0` (the app user needs write access,
     usually via the `lp` group), or set up a raw CUPS queue.
   - **CUPS** → `lpadmin -p ml4410 -E -v <device-uri> -m raw`, then `Transport: "Cups"` with `Queue: "ml4410"`.
   - **Development** → `File` writes `.prn` files you can inspect, or send to a printer later with
     `cat file.prn > /dev/usb/lp0` or `lp -o raw`.
3. If lines step down without returning to the left margin, or print double-spaced, toggle the printer's
   auto-CR / auto-LF setting. WebPos always sends CR LF.

The `.txt` download, at `/reports/{SalesSummary|ItemSales|TransactionJournal|PriceList}.txt?from=yyyy-MM-dd&to=yyyy-MM-dd`,
contains exactly what is sent to the printer, without the init bytes.

## Notes and possible next steps

- Customer-mode scores last only until the page is reloaded. A saved high-score table per user, and a second
  "customer display" window showing the basket on a tablet next to the scanner, would be fun next steps.
- Managers can't yet correct a shift (someone forgot to clock out); the time-card report shows it as still on the clock.
- Approved removals of items from an open sale aren't logged anywhere (only price changes and voids are saved,
  on the sale). A void/no-sale audit log and a report of price changes would be natural additions for loss prevention.
- Run it over HTTPS (or behind a TLS-terminating proxy) before using it beyond a trusted LAN, since passwords are
  sent in the login form.
- There is one tax rate for all items. Per-item tax classes, discounts, split payments, refunds, stock levels and
  customer receipts (which could reuse the same raw-text printer path) are natural additions.
- Migrations run automatically on startup, which suits a single-store deployment. Run them as a separate step if
  you need tighter control.
