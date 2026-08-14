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
