/**
 * How long the DNS page waits for the local controller to look something up, and
 * what it tells the local controller about a name server, asked directly.
 *
 * Run with `npm test`. What is pinned is what was wrong: the page added the
 * servers' timeouts up, as if the local controller asked them one after another, when
 * it asks all of them at once - and it is what a server asked again takes
 * that the page has to be willing to wait for, not what it takes once. And
 * that a name server goes back to the local controller with what it is held
 * to, where it shows a certificate to hold it to, and with nothing the
 * controller only said about it.
 */

import { strict as assert }  from 'node:assert';
import { readFileSync }      from 'node:fs';
import { registerHooks }     from 'node:module';
import { describe, it }      from 'node:test';

import type { DNSConfiguration, DNSServer } from '../api/client';

// The page's helpers are written for webpack, which does not want the
// extension in a relative import, and they import what a server is held to
// from pins.ts; Node wants the extension. One hook puts it back - see the
// client's test.
registerHooks({
    resolve(specifier, context, next) {
        return specifier.startsWith('.') && !specifier.endsWith('.ts')
                   ? next(`${specifier}.ts`, context)
                   : next(specifier, context);
    }
});

const { allServersTake, entryOf, isEncrypted, oneServerTakes } = await import('./dnsServers.ts');


/** A name server as the local controller shows it, with a timeout of its own or none. */
const server = (queryTimeoutSeconds: number | null = null): DNSServer =>
    ({ address: '192.0.2.1', port: 53, transport: 'UDP', queryTimeoutSeconds });

/** What the local controller says about its name resolution, with these servers. */
const configuration = (servers: DNSServer[], queryTimeoutSeconds = 10, maxRetries = 1): DNSConfiguration =>
    ({
        enabled:   true,
        servers,
        settings:  { queryTimeoutSeconds, recursionDesired: null, useCache: true, dnssecOK: false,
                     followCNAMEs: true, maxCNAMEFollows: 8, maxRetries },
        fixed:     {},
        limits:    { maxServers: 16, maxQueryTimeout: 120, transports: [ 'UDP' ], recordTypes: [ 'A' ] },
        file:      'configuration.json'
    });


describe('one name server', () => {

    it('takes its own timeout for every time it is asked', () => {

        assert.equal(oneServerTakes(configuration([ server(3) ], 10, 1), 0), 6);
        assert.equal(oneServerTakes(configuration([ server(3) ], 10, 0), 0), 3);

    });

    it('and the client\'s where it has none of its own', () => {

        assert.equal(oneServerTakes(configuration([ server() ], 10, 1), 0), 20);

    });

});


describe('all of them', () => {

    it('take as long as the slowest of them and not their sum, because they are asked at once', () => {

        // One after another, these three would take 16 seconds.
        assert.equal(allServersTake(configuration([ server(3), server(3), server(10) ], 5, 0)), 10);

    });

    it('each for every time it is asked', () => {

        assert.equal(allServersTake(configuration([ server(3), server() ], 5, 1)), 10);

    });

    it('take nothing when there are none', () => {

        assert.equal(allServersTake(configuration([])), 0);
        assert.equal(allServersTake(null),              0);

    });

});


describe('a name server told to the local controller', () => {

    const root = 'a'.repeat(64);

    /** A name server over TLS as the local controller shows it: held to a root, judged, and known. */
    const overTLS = (): DNSServer => ({
        address: '1.1.1.1', port: 853, transport: 'TLS', queryTimeoutSeconds: null,
        rootFingerprint: root, trustOnFirstUse: 'root',
        heldTo:    { certificate: null, root, certificates: [], roots: [ root ], onMismatch: 'refuse', trustOnFirstUse: 'root' },
        judgement: null,
        known:     { certificate: 'b'.repeat(64), root, since: '2026-09-27T10:00:00.0000000+00:00' }
    });

    it('goes with what it is held to, and without what the controller only said about it', () => {

        assert.deepEqual(entryOf(overTLS()),
                         { address: '1.1.1.1', port: 853, transport: 'TLS', queryTimeoutSeconds: null,
                           rootFingerprint: root, trustOnFirstUse: 'root' });

    });

    it('lets go of its pins once it is asked over a transport that shows no certificate, which the controller would refuse', () => {

        // What was measured: 1.1.1.1 over TLS had learned its root, was
        // switched to UDP on the page and saved, and the local controller
        // refused the whole save - "'dns.servers[0]' is held to a certificate
        // and asked over UDP, which shows none" - with no way on the page to
        // take the pins off.
        assert.deepEqual(entryOf({ ...overTLS(), transport: 'UDP', port: 53 }),
                         { address: '1.1.1.1', port: 53, transport: 'UDP', queryTimeoutSeconds: null });

    });

    it('is asked over TLS or HTTPS in all its forms, and over nothing else, for a certificate', () => {

        assert.deepEqual([ 'TLS', 'HTTPS', 'HTTPS_Binary', 'HTTPS_JSON', 'HTTPS_GET' ].map(isEncrypted), [ true, true, true, true, true ]);
        assert.deepEqual([ 'UDP', 'TCP', 'HTTP', 'HTTP_Binary', 'HTTP_JSON' ].map(isEncrypted),          [ false, false, false, false, false ]);

    });

    it('is what the DNS page saves, and what it compares with to say whether anything was changed', () => {

        // Asked of the page's source, as Node has no browser to open it in.
        const page = readFileSync(new URL('./dns.ts', import.meta.url), 'utf-8');

        assert.match(page, /servers:\s+servers\.filter\(.*\)\.map\(entryOf\),/,
                     'the DNS page sends its name servers as it has them, pins on UDP and all');

        assert.match(page, /JSON\.stringify\(servers\.map\(entryOf\)\)/,
                     'the DNS page says whether its name servers were changed from what it would not send');

    });

});


describe('what the DNS page does with what a name server is held to', () => {

    // Asked of the page's source, as Node has no browser to open it in, and
    // the views it draws with cannot be loaded here.
    const page = readFileSync(new URL('./dns.ts', import.meta.url), 'utf-8');

    it('takes it from the server\'s dialog into the list on screen, which Save sends', () => {

        assert.match(page, /servers\[index\] = withPins\(servers\[index\], pins\.draft\);/,
                     'the DNS page does not take a server\'s pins from its dialog into the list');

    });

    it('says of a server asked over a transport that shows no certificate that it lets go of its pins once saved', () => {

        assert.match(page, /: saysAnything\(pinsIn\(server\)\)/,
                     'the DNS page lets a server go of its pins without saying so first');

    });

});
