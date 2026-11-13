/**
 * The CSMS page drawn, in a document of happy-dom, against a stand-in
 * controller: it says whether the controller reports to a CSMS and how its
 * lines are kept, sends whoever looks for a line itself to the Connections,
 * a form saved says what the controller took, and one refused keeps what is
 * typed and says why. What is a connection's to say - where a line goes, its
 * security profile, the version of HTTP it is opened over - is neither shown
 * nor sent.
 */

import { asked, field, open, refused, said, settled, submit, type, until, type Asked } from '../../test/controller.ts';

import { strict as assert }  from 'node:assert';
import { describe, it }      from 'node:test';

import type { CSMSConfiguration } from '../api/client.ts';

const { csmsPage } = await import('./csms.ts');


/** What a controller set up before its lines were connections may still say in its "csms" section. */
interface SaidBefore {
    url?:              string;
    securityProfile?:  number;
    httpVersion?:      string;
}

let held: CSMSConfiguration & SaidBefore;

/** Refuses the settings where told to. */
let refuseSettings = false;

function controller({ method, path, body }: Asked): unknown {

    if (path === '/certificates')
        return { certificates: { tlsIdentity: [] } };

    if (path === '/configuration/csms' && method === 'PUT') {

        const update = body as Partial<CSMSConfiguration> & SaidBefore;

        // As the controller refuses them: they are a connection's.
        if (update.url !== undefined || update.securityProfile !== undefined || update.httpVersion !== undefined)
            return refused(400, 'The address of a line up to the CSMS is a connection\'s: it is changed under Connections.');

        if (refuseSettings)
            return refused(400, 'A line is dialled again after 600 seconds at the latest.');

        // A controller takes what it takes: a ping every half minute at the most often.
        held = { ...held, ...update, pingEvery: Math.max(update.pingEvery ?? held.pingEvery, 30) } as CSMSConfiguration & SaidBefore;

        return held;

    }

    if (path === '/configuration/csms')
        return held;

    return undefined;

}

async function opened(as: Partial<CSMSConfiguration & SaidBefore> = {}): Promise<HTMLElement> {
    held = {
        enabled: true, nextHopNodeId: 'CSMS', subprotocols: [ 'ocpp2.1' ],
        checkCertificateRevocation: false, pingEvery: 60, requestTimeout: 30, reconnectInitialDelay: 5, reconnectMaxDelay: 300,
        networkingMode: 'overlay',
        state: { connected: false, connectedSince: null, lastProblem: null, connections: 1, waitingForARestart: [] },
        ...as
    };
    refuseSettings = false;
    return open(csmsPage, '/configuration/csms', [ 'csms:read', 'csms:edit', 'certificates:read' ],
                controller, root => root.querySelector('#connection-form') !== null);
}


describe('the CSMS page', () => {

    it('sends whoever looks for a line itself to the Connections, and says how many are written down', async () => {

        const root = await opened();

        assert.ok(root.querySelector('#lines-are-connections a[href$="/configuration/connections"]') !== null,
                  'the lines are not looked for under Connections');
        assert.equal(root.querySelector('#lines')?.textContent, '1');
        assert.equal(root.querySelector('#no-line'), null, 'a controller with a line is told it has none');

    });

    it('says that a controller that is to report to a CSMS has no line written down', async () => {

        const root = await opened({ state: { connected: false, connectedSince: null, lastProblem: null, connections: 0, waitingForARestart: [] } });

        assert.match(root.querySelector('#no-line')?.textContent ?? '', /no line up to one is written down/);

    });

    it('sends whoever looks for the identity a line holds up to the Identities, where who the controller is as a client is kept', async () => {

        const root = await opened();

        assert.ok(root.querySelector('a[href$="/configuration/identities"]') !== null, 'the identity is not looked for on the Identities');
        assert.ok(root.querySelector('a[href$="/configuration/certificates"]') === null, 'the identity is looked for among the certificates, which keep no key');

    });

    it('neither shows nor sends the address of a line, its security profile or the version of HTTP it is opened over', async () => {

        // As a section written before the lines were connections still says them.
        const root = await opened({ url: 'wss://csms.example.org/ocpp/lc001', securityProfile: 2, httpVersion: '3',
                                    state: { connected: false, connectedSince: null, lastProblem: null, connections: 1,
                                             waitingForARestart: [ 'url', 'securityProfile', 'httpVersion', 'pingEvery' ] } });

        for (const name of [ 'url', 'securityProfile', 'httpVersion' ])
            assert.equal(root.querySelector(`[name="${name}"]`), null, `${name} is offered`);

        assert.doesNotMatch(root.textContent ?? '', /csms\.example\.org/, 'the address of the line is shown');

        // What waits for a restart is said of what this page saves, and of nothing else.
        const waiting = root.querySelector('#waiting-for-a-restart')?.textContent ?? '';

        assert.match(waiting, /pingEvery\s+was changed/);
        assert.doesNotMatch(waiting, /url|securityProfile|httpVersion/);

        submit(root, '#connection-form');
        await until(() => asked.some(one => one.method === 'PUT') && root.querySelector('#connection-note')?.textContent === '',
                    'the settings were not saved');

        const sent = asked.find(one => one.method === 'PUT' && one.path === '/configuration/csms')?.body as Record<string, unknown>;

        assert.deepEqual(Object.keys(sent).sort(), [ 'clientCertificate', 'enabled', 'pingEvery', 'reconnectInitialDelay' ]);
        assert.equal(root.querySelector('#connection-error')?.textContent, '', 'the controller refused what was sent');

    });

    it('shows the settings as the controller took them once they are saved, with nothing left to save', async () => {

        const root = await opened();
        const ping = field(root, '#connection-form', 'pingEvery');

        ping.value = '7';

        submit(root, '#connection-form');
        await until(() => held.pingEvery === 30 && root.querySelector('#connection-note')?.textContent === '', 'the settings were not saved');

        assert.equal((asked.find(one => one.method === 'PUT')?.body as { pingEvery: number }).pingEvery, 7);
        assert.equal(field(root, '#connection-form', 'pingEvery').value,         '30', 'the field says what was typed, not what the controller took');
        assert.equal(field(root, '#connection-form', 'pingEvery').defaultValue,  '30');

    });

    it('keeps what is typed into settings the controller refused, and says why', async () => {

        const root  = await opened();
        const again = field(root, '#connection-form', 'reconnectInitialDelay');

        refuseSettings = true;
        again.value = '900';

        submit(root, '#connection-form');
        await until(() => root.querySelector('#connection-error')?.textContent !== '', 'the refusal was not said');

        // And drawn again from what the controller has, which it is not.
        await until(() => asked.filter(one => one.method === 'GET' && one.path === '/configuration/csms').length === 2,
                    'the page did not ask what the controller has after the refusal');
        await settled();

        assert.equal(root.querySelector('#connection-error')!.textContent, 'A line is dialled again after 600 seconds at the latest.');
        assert.equal(field(root, '#connection-form', 'reconnectInitialDelay').value, '900', 'what was typed went');

    });

});


describe('the Reload of the CSMS page', () => {

    it('asks before it throws away what is typed, and then asks the controller again', async () => {

        const root = await opened();

        type(field(root, '#connection-form', 'pingEvery'), '90');

        const before = asked.length;

        root.querySelector<HTMLButtonElement>('.page-actions #reload')!.click();

        await until(() => asked.slice(before).some(one => one.method === 'GET' && one.path === '/configuration/csms'),
                    'the controller was not asked again');

        assert.equal(said.length, 1, `asked ${said.length} times before the typed ping was thrown away`);
        assert.match(said[0]!, /not been told about/);

    });

});
