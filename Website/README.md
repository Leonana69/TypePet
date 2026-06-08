# TypePet — product website

The marketing/landing site for [TypePet](../README.md). It's a single static page (no build step,
no framework) that you can host on **GitHub Pages** or any static host.

```
Website/
├── index.html        # the page
├── styles.css        # all styles
├── main.js           # walking-pet animation + interactive demos
├── assets/
│   ├── icon.png      # mascot / favicon
│   ├── og-image.png  # Open Graph / Twitter social banner (1200×630)
│   └── sprites/      # real character frames, reused for the on-page pet
├── .nojekyll         # tell Pages to serve files as-is (no Jekyll)
├── robots.txt
└── sitemap.xml
```

The sprite frames and icon are copied from the app's own `Assets/`, so the pet you see walking on the
site is rendered from the exact footage the desktop pet uses.

## Host it on GitHub Pages (recommended)

A workflow at [`.github/workflows/pages.yml`](../.github/workflows/pages.yml) already deploys this
folder. You only need to flip Pages on once:

1. Push this repo to GitHub (the workflow triggers on pushes to `main` that touch `Website/`).
2. Go to **Settings → Pages → Build and deployment** and set **Source = “GitHub Actions”**.
3. Re-run the **Deploy website** workflow (Actions tab → *Deploy website* → *Run workflow*), or push
   any change under `Website/`.

The site goes live at:

```
https://<your-user>.github.io/<your-repo>/
```

For this repo that's **https://leonana69.github.io/MaplePet/** (the URL baked into the page's
`canonical`, `og:url`, `og:image`, and `sitemap.xml`).

> **Renaming the repo or using a custom domain?** All asset links are relative, so the page itself
> keeps working anywhere. Just update the four absolute URLs above (search the repo for
> `leonana69.github.io/MaplePet`) so social previews and the sitemap point at the new address. For a
> custom domain, add a `CNAME` file here containing the domain.

## Preview locally

It's plain static files — open `index.html` directly, or serve the folder:

```bash
cd Website
python3 -m http.server 8000      # then open http://localhost:8000
```

A static server is closest to how Pages serves it (relative paths, no `file://` quirks).

## Alternatives

- **Any static host** (Netlify, Cloudflare Pages, Vercel, S3): point it at this `Website/` folder.
- **Deploy-from-branch Pages**: GitHub Pages can only serve from a repo's root or `/docs`, not an
  arbitrary `Website/` folder — that's why this project uses the GitHub Actions workflow above. If you
  prefer branch deployment, copy the contents of `Website/` into `/docs` (or the repo root) instead.

## Notes

- No tracking, no cookies, no external JS — only the Google Fonts stylesheet is fetched remotely.
- Animations respect `prefers-reduced-motion`: the pet stands still and demos render statically.
