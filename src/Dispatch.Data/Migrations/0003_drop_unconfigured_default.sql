-- Onboarding change: don't ship a pre-seeded placeholder relay.
-- 0001 seeded a single 'default'/'Unconfigured' catch-all so the routing engine always had a fallback.
-- That confused first-run users (an empty relay you must go edit). Instead the first-run wizard creates
-- the first real relay, which becomes the catch-all automatically (SqlRelayRepository.CreateAsync).
--
-- Remove the placeholder ONLY if it was never configured (no provider chosen for it in the config table,
-- keys 'relay:{id}:provider'), so we never delete a relay an operator has actually set up. Routing
-- degrades cleanly with no default: mail permanently fails with a clear "no provider configured" result
-- until the wizard/first relay is added.
DELETE FROM relays
WHERE provider = 'Unconfigured' AND is_default
  AND NOT EXISTS (
    SELECT 1 FROM config c
    WHERE c."key" = 'relay:' || relays.id::text || ':provider'
  );
