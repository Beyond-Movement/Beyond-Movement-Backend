# Brand assets

Files here are served publicly by the API, with no authentication. Put nothing else in this
folder.

## logo.png — needed for branded email

Save the Beyond Movement logo here as **`logo.png`**, then point the email templates at it:

```bash
# local
dotnet user-secrets set "Email:LogoUrl" "http://localhost:5229/brand/logo.png" --project src/BeyondMovement.Api

# deployed — must be the public HTTPS address of the API
Email__LogoUrl=https://api.yourdomain.com/brand/logo.png
```

Leave `Email:LogoUrl` empty and the masthead falls back to the wordmark set as type, which is
also what recipients see when images are switched off.

### Why a URL and not an embedded image

Mail clients fetch the logo over the internet when the message is opened. A file committed to
this repository is not reachable until the API is deployed at a public HTTPS address — and a
`data:` URI is not a workaround, because Gmail strips them, so the logo would be missing for
most recipients while looking correct in local testing.

### What the file should be

- **PNG with a transparent background**, so the pale yellow masthead shows through.
- **Roughly 264×264** — twice the 132px it is displayed at, so it stays sharp on retina screens.
- **Under about 100 KB.** Some clients clip large messages, and a heavy image delays rendering.
- Square. The template reserves a square area and sets explicit `width` and `height`, which
  stops Outlook reflowing the masthead before the image loads.

---

## instapay-qr.png — needed for the athlete Pay screen

Save the coach's InstaPay QR code here as **`instapay-qr.png`**, then point the payment
configuration at it:

```bash
# local
dotnet user-secrets set "Payments:InstaPay:QrImageUrl" "http://localhost:5229/brand/instapay-qr.png" --project src/BeyondMovement.Api

# deployed - must be the public HTTPS address of the API
Payments__InstaPay__QrImageUrl=https://api.yourdomain.com/brand/instapay-qr.png
```

`GET /api/v1/payments/instapay-instructions` returns this URL to the app, which renders it beside
the payment link. It is served from this folder, so it is reachable **without a bearer token** -
that is deliberate and required, because the app fetches it with an ordinary image request and an
`<img>` cannot carry an Authorization header.

Nothing here is a secret. A payment QR code is meant to be shown to whoever is paying.

### What the file should be

- **PNG**, square, with the code's quiet zone (the white margin) intact - scanners need it.
- **At least 512x512.** It is displayed large enough to be scanned off the screen by a second
  phone, which is the common case: the athlete opens InstaPay on the same device, or a parent
  scans it from the athlete's screen.
- **Do not crop the "Powered by InstaPay" strip or the handle underneath** if the exported image
  includes them. They are how the athlete confirms they are paying the right account before
  sending money.

### If the coach's InstaPay account changes

Replace this file and update `Payments:InstaPay:PaymentUrl` and `RecipientHandle` together. They
are shown side by side, and a QR code that disagrees with the handle printed next to it reads as
a scam rather than a typo.
