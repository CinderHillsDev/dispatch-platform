// Astro integration: after the static build, prefix the configured `base` onto root-relative
// links/images in the emitted HTML.
//
// Why a post-build pass: Starlight applies `base` to all the links IT controls (sidebar, nav, asset
// URLs), but root-relative links written inside Markdown bodies (e.g. `/start/quickstart/`) are left
// untouched, and Astro's `markdown.rehypePlugins` don't reliably reach Starlight's content pipeline.
// Rewriting the final HTML is bulletproof and keeps the Markdown source base-less - so moving to a
// custom domain (CNAME) is a config-only change: set `base: '/'` and these rewrites become no-ops.
import { readdir, readFile, writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { join, extname } from 'node:path';

export default function baseHref(base) {
  const prefix = base.replace(/\/$/, ''); // "/Dispatch-SMTP-Relay" or ""
  return {
    name: 'dispatch-base-href',
    hooks: {
      'astro:build:done': async ({ dir, logger }) => {
        if (!prefix) return; // base '/' → nothing to do (custom-domain case)
        // Match href/src="/path" where the path is NOT protocol-relative ("//"), NOT already
        // base-prefixed, and NOT a bare anchor. Only app-internal, root-relative URLs.
        const re = new RegExp(
          `(href|src)="(/(?!/)(?!${prefix.slice(1)}/)[^"]*)"`,
          'g',
        );
        const root = fileURLToPath(dir);
        let changed = 0;
        const walk = async (d) => {
          for (const ent of await readdir(d, { withFileTypes: true })) {
            const p = join(d, ent.name);
            if (ent.isDirectory()) await walk(p);
            else if (extname(ent.name) === '.html') {
              const html = await readFile(p, 'utf8');
              const out = html.replace(re, (_m, attr, path) => `${attr}="${prefix}${path}"`);
              if (out !== html) { await writeFile(p, out); changed++; }
            }
          }
        };
        await walk(root);
        logger.info(`base-href: prefixed root-relative links in ${changed} file(s) with ${prefix}`);
      },
    },
  };
}
