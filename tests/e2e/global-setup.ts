// The API reports ready before Keycloak has finished importing its realm (a cold start takes a minute or more), so
// wait for the identity provider as well. E2E_IDP_URL is its OIDC discovery document; skip by leaving it empty.
export default async function globalSetup() {
  const url = process.env.E2E_IDP_URL ?? "http://localhost:8081/realms/kb/.well-known/openid-configuration";
  if (!url) return;

  const deadline = Date.now() + 180_000;
  while (Date.now() < deadline) {
    try {
      if ((await fetch(url)).ok) return;
    } catch {
      // not up yet
    }
    await new Promise((r) => setTimeout(r, 3_000));
  }
  throw new Error(`Identity provider not ready after 3 minutes: ${url}`);
}
