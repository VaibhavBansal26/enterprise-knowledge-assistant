import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from "react";
import type { User } from "oidc-client-ts";
import { setAuthHandlers } from "../api/client";
import { rolesFromAccessToken, userManager } from "./oidc";

interface AuthValue {
  username: string;
  roles: string[];
  isAdmin: boolean;
  /** Admins and editors may create and change content; the API enforces this too. */
  canWrite: boolean;
  signOut: () => void;
}

const AuthContext = createContext<AuthValue | null>(null);

export function useAuth(): AuthValue {
  const value = useContext(AuthContext);
  if (!value) throw new Error("useAuth must be used inside <AuthProvider>");
  return value;
}

function signIn(returnTo?: string) {
  return userManager.signinRedirect({
    state: { returnTo: returnTo ?? window.location.pathname + window.location.search },
  });
}

// React StrictMode runs effects twice in development. Completing the login callback twice would fail
// (the one-time `state` is already consumed), so the bootstrap runs exactly once per page load.
let bootstrap: Promise<User | null> | null = null;

function startOnce(): Promise<User | null> {
  bootstrap ??= (async () => {
    const params = new URLSearchParams(window.location.search);
    if (params.has("code") && params.has("state")) {
      const user = await userManager.signinCallback();
      const returnTo = (user?.state as { returnTo?: string } | undefined)?.returnTo ?? "/";
      window.history.replaceState({}, "", returnTo);
      return user ?? null;
    }

    const existing = await userManager.getUser();
    if (existing && !existing.expired) return existing;

    await signIn(); // leaves the page; the promise never needs to resolve
    return null;
  })();
  return bootstrap;
}

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<User | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    startOnce()
      .then((u) => !cancelled && u && setUser(u))
      .catch((e: unknown) => !cancelled && setError(e instanceof Error ? e.message : "Sign-in failed"));

    const onLoaded = (u: User) => setUser(u);
    const onLost = () => void signIn();
    userManager.events.addUserLoaded(onLoaded);
    userManager.events.addAccessTokenExpired(onLost);
    userManager.events.addSilentRenewError(onLost);

    // The API client asks for a token per request, and re-authenticates if the API still says 401.
    setAuthHandlers({
      getToken: async () => (await userManager.getUser())?.access_token,
      onUnauthorized: () => void signIn(),
    });

    return () => {
      cancelled = true;
      userManager.events.removeUserLoaded(onLoaded);
      userManager.events.removeAccessTokenExpired(onLost);
      userManager.events.removeSilentRenewError(onLost);
    };
  }, []);

  const signOut = useCallback(() => void userManager.signoutRedirect(), []);

  const value = useMemo<AuthValue | null>(() => {
    if (!user) return null;
    const roles = rolesFromAccessToken(user.access_token);
    return {
      username: user.profile.preferred_username ?? user.profile.sub,
      roles,
      isAdmin: roles.includes("admin"),
      canWrite: roles.includes("admin") || roles.includes("editor"),
      signOut,
    };
  }, [user, signOut]);

  if (error) {
    return (
      <main style={{ fontFamily: "system-ui", maxWidth: 640, margin: "4rem auto" }}>
        <h1>Sign-in failed</h1>
        <p role="alert">{error}</p>
        <button onClick={() => void signIn("/")}>Try again</button>
      </main>
    );
  }

  if (!value) {
    return <p style={{ fontFamily: "system-ui", margin: "4rem auto", maxWidth: 640 }}>Signing in…</p>;
  }

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}
