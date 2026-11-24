// Decides whether the load-test tool may act on a set of endpoints. The tool creates users and organizations and
// sends many messages, so a target must be named explicitly and must not be the stack someone develops in.

/** The fixed ports of the development AppHost (Keycloak, control plane, Mailpit, cells). An isolated run uses random ports. */
export const DEVELOPMENT_PORTS = [1025, 5100, 5200, 5201, 5202, 5203, 7100, 8025, 8443];

const LOCAL_HOSTS = ['localhost', '127.0.0.1', '[::1]'];

/**
 * Throws unless every target is set, local (or LOADTEST_ALLOW_REMOTE=yes) and not on a development port
 * (or LOADTEST_ALLOW_DEVELOPMENT_STACK=yes). `targets` maps a name (the variable or fixture field it came from) to a URL.
 */
export function assertSafeTargets(targets, env = process.env) {
  for (const [name, value] of Object.entries(targets)) {
    if (value === undefined || value === null || value === '') {
      throw new Error(`${name} is not set. There is no default target: name the load-test stack's endpoints explicitly.`);
    }
    let url;
    try {
      url = new URL(value);
    } catch {
      throw new Error(`${name} is not a URL.`);
    }
    if (!LOCAL_HOSTS.includes(url.hostname) && env.LOADTEST_ALLOW_REMOTE !== 'yes') {
      throw new Error(`Refusing the non-local target ${name}. Set LOADTEST_ALLOW_REMOTE=yes only for a dedicated load-test stack, never production.`);
    }
    const port = Number(url.port || (url.protocol === 'https:' ? 443 : 80));
    if (LOCAL_HOSTS.includes(url.hostname) && DEVELOPMENT_PORTS.includes(port) && env.LOADTEST_ALLOW_DEVELOPMENT_STACK !== 'yes') {
      throw new Error(
        `Refusing ${name}: port ${port} belongs to the development stack. Start an isolated stack and use its endpoints; ` +
          'LOADTEST_ALLOW_DEVELOPMENT_STACK=yes overrides this (for example to clean up after a mistake).',
      );
    }
  }
}
