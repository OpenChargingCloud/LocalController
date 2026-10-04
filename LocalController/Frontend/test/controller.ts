/*
 * A local controller that is a stand-in for fetch, and a document of happy-dom
 * to draw a page into: what the pages' tests in src/pages/*.test.ts share.
 * What every kind's stand-in does is WWCP_Node's, @node/../test/node.ts; what
 * is here is the local controller's own.
 *
 * Imported first, before a page: lit-html looks for the document as it is
 * loaded, and the pages load it.
 *
 *   import { open, ... } from '../../test/controller.ts';
 *   const { csmsPage } = await import('./csms.ts');
 */

import { standIn } from '@node/../test/node.ts';

import type { OCPPServerConfiguration } from '../src/api/client.ts';

export * from '@node/../test/node.ts';

standIn({ name: 'Local Controller', icon: 'fa-sitemap' });


/** The charging station server, as a controller that has just been set up has it. */
export function anOCPPServer(): OCPPServerConfiguration {
    return {
        enabled:                     false,
        address:                     null,
        port:                        8443,
        securityProfiles:            [ 1, 2, 3 ],
        subprotocols:                [ 'ocpp2.1' ],
        reachableAs:                 [ 'lc001.example.org' ],
        minTLSVersion:               '1.2',
        checkCertificateRevocation:  false,
        maxConnections:              100,
        pingEverySeconds:            60,
        logging:                     { connections: true, authentication: true, messages: false, payloads: false,
                                       payloadsUntil: null, payloadsNow: false, pings: false },
        state:                       { running: false, tls: false, url: 'wss://lc001.example.org:8443', connections: 0,
                                       stationLogins: 0, trustedChains: 1, hasCertificate: false, waitingForARestart: [] },
        limits:                      { subprotocols: [ 'ocpp1.6', 'ocpp2.0.1', 'ocpp2.1' ], securityProfiles: [ 1, 2, 3 ],
                                       tlsVersions: [ '1.2', '1.3' ], maxConnections: 1000, maxReachableAs: 8,
                                       suggestedPayloadWindowSeconds: 3600 },
        file:                        'ocppServer.json'
    };
}
