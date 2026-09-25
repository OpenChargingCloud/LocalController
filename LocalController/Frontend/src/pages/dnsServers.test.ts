/**
 * How long the DNS page waits for the local controller to look something up, asked
 * directly.
 *
 * Run with `npm test`. What is pinned is what was wrong: the page added the
 * servers' timeouts up, as if the local controller asked them one after another, when
 * it asks all of them at once - and it is what a server asked again takes
 * that the page has to be willing to wait for, not what it takes once.
 */

import { strict as assert }  from 'node:assert';
import { describe, it }      from 'node:test';

import type { DNSConfiguration, DNSServer } from '../api/client';
import { allServersTake, oneServerTakes } from './dnsServers.ts';


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
