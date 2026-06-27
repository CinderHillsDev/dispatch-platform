// @ts-check
import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';
import baseHref from './src/lib/base-href-integration.mjs';

// NOTE on custom domains (CNAME): this is currently a GitHub *project* site, so it is served under
// the base path `/Dispatch-SMTP-Relay/`. To move to a custom domain later, change `site` to the
// domain and `base` to '/', then drop a `CNAME` file in `website/public/`. Because every internal
// link is base-relative (Starlight handles its own; the landing page uses import.meta.env.BASE_URL),
// that switch is the only change needed.
const site = 'https://chrismuench.github.io';
const base = '/Dispatch-SMTP-Relay';

export default defineConfig({
  site,
  base,
  trailingSlash: 'always',
  integrations: [
    // Prefix `base` onto base-less root-relative links in Markdown bodies (post-build). Keeps the
    // Markdown source base-less so a future custom domain (base '/') needs no content edits.
    baseHref(base),
    starlight({
      title: 'Dispatch SMTP Relay',
      description:
        'Self-hosted .NET SMTP relay — forward mail from your apps to a dozen providers, capture mail locally for testing, with a durable spool and a live dashboard.',
      social: [
        { icon: 'github', label: 'GitHub', href: 'https://github.com/chrismuench/Dispatch-SMTP-Relay' },
      ],
      customCss: ['./src/styles/custom.css'],
      // The site root `/` is a custom landing page (src/pages/index.astro); "Home" links back to it.
      logo: { src: './src/assets/logo.svg', replacesTitle: false },
      editLink: {
        baseUrl: 'https://github.com/chrismuench/Dispatch-SMTP-Relay/edit/main/website/',
      },
      sidebar: [
        { label: 'Start here', items: [{ autogenerate: { directory: 'start' } }] },
        { label: 'Sending mail', items: [{ autogenerate: { directory: 'sending' } }] },
        { label: 'Relay providers', items: [{ autogenerate: { directory: 'providers' } }] },
        { label: 'Routing', link: '/routing/' },
        { label: 'Configuration', items: [{ autogenerate: { directory: 'configuration' } }] },
        { label: 'Security', link: '/security/' },
        { label: 'Deployment', items: [{ autogenerate: { directory: 'deployment' } }] },
        { label: 'Operations', items: [{ autogenerate: { directory: 'operations' } }] },
        { label: 'Reference', items: [{ autogenerate: { directory: 'reference' } }] },
        { label: 'Project', items: [{ autogenerate: { directory: 'project' } }] },
      ],
    }),
  ],
  // The retired standalone pages (/deploy.html, /appliance.html) are redirected via meta-refresh
  // stubs in website/public/ — reliable on static GitHub Pages output.
});
