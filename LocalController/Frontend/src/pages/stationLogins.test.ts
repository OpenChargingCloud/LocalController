/**
 * The logins and groups drawn, in a document of happy-dom, against a
 * stand-in controller: a station half typed - and its focus - outlives
 * another being switched off, a switch or a chooser the controller refused
 * says what it has, the settings of one group do not keep what was typed
 * into another's, and a station added empties the form and says its password
 * once.
 */

import { asked, change, field, open, refused, said, settled, submit, until, type Asked } from '../../test/controller.ts';

import { strict as assert }  from 'node:assert';
import { describe, it }      from 'node:test';

import type { LoginGroup, StationLogin, StationLogins } from '../api/client.ts';

const { stationLoginsPage } = await import('./stationLogins.ts');


function aGroup(id: string, builtIn = false): LoginGroup {
    return { id, name: `Group ${id}`, enabled: true, builtIn, addedAt: '2026-10-01T00:00:00Z',
             authMethods: [ 'basic', 'totp', 'certificate' ], securityProfiles: [ 1, 2, 3 ], members: 0 };
}

function aStation(id: string, group = 'default'): StationLogin {
    return { id, group, enabled: true, addedAt: '2026-10-01T00:00:00Z', hasPassword: true, hasTOTP: false };
}

let held: StationLogins;

/** Refuses every change to a station where told to. */
let refuseChanges = false;

function controller({ method, path, body }: Asked): unknown {

    if (path === '/configuration/ocpp-server/stations' && method === 'GET')
        return held;

    if (path === '/configuration/ocpp-server/stations' && method === 'POST') {
        const added = body as { id: string; group: string; note: string };
        held = { ...held, stations: [ ...held.stations, { ...aStation(added.id, added.group), note: added.note } ] };
        return { id: added.id, password: 'made up, shown once', stations: held };
    }

    const one = /^\/configuration\/ocpp-server\/stations\/([^/]+)$/.exec(path);

    if (one && method === 'PUT') {
        if (refuseChanges)
            return refused(409, 'the group is full');
        held = { ...held, stations: held.stations.map(station => station.id === one[1] ? { ...station, ...(body as object) } : station) };
        return held;
    }

    return undefined;

}

async function opened(): Promise<HTMLElement> {
    held = { file: 'stations.json', enabled: 2, maxStations: 100, maxGroups: 8, minPasswordLength: 16, minSecretLength: 20,
             defaultGroup: 'default', groups: [ aGroup('default', true), aGroup('field-test') ],
             stations: [ aStation('cs001'), aStation('cs002') ] };
    refuseChanges = false;
    return open(stationLoginsPage, '/configuration/ocpp-server/logins', [ 'stations:read', 'stations:edit' ],
                controller, root => root.querySelector('#station-form') !== null);
}

const switchOf  = (root: HTMLElement, id: string) => root.querySelector<HTMLInputElement>(`.station-enabled[data-id="${id}"]`)!;
const chooserOf = (root: HTMLElement, id: string) => root.querySelector<HTMLSelectElement>(`.station-group[data-id="${id}"]`)!;
const putsSent  = () => asked.filter(one => one.method === 'PUT').length;


describe('the logins and groups', () => {

    it('keep a station half typed, and its focus, while another is switched off', async () => {

        const root = await opened();
        const id   = field(root, '#station-form', 'id');
        const note = field(root, '#station-form', 'note');

        id.value   = 'cs003';
        note.value = 'Ladepunkt 3';
        note.focus();

        change(switchOf(root, 'cs001'), false);
        await until(() => held.stations[0]!.enabled === false && root.querySelector('tr.dimmed') !== null,
                    'the station switched off was not drawn so');

        assert.ok(field(root, '#station-form', 'note') === note, 'the field was made anew');
        assert.equal(id.value,                'cs003');
        assert.equal(note.value,              'Ladepunkt 3');
        assert.ok(document.activeElement === note, 'the focus went');

    });

    it('put a switch and a chooser the controller refused back to what it has', async () => {

        const root = await opened();

        refuseChanges = true;

        change(switchOf(root, 'cs001'), false);
        await until(() => said.length === 1 && asked.filter(one => one.method === 'GET').length === 2, 'the refused switch was not loaded again');
        await settled();

        change(chooserOf(root, 'cs002'), 'field-test');
        await until(() => said.length === 2 && asked.filter(one => one.method === 'GET').length === 3, 'the refused move was not loaded again');
        await settled();

        assert.equal(putsSent(), 2);
        assert.match(said[0]!, /the group is full/);
        assert.equal(switchOf(root, 'cs001').checked,  true,       'the switch says what was clicked');
        assert.equal(chooserOf(root, 'cs002').value,   'default',  'the chooser says what was chosen');

    });

    it('open the settings of another group without what was typed into the first one\'s', async () => {

        const root = await opened();

        root.querySelector<HTMLButtonElement>('.group-edit[data-id="default"]')!.click();
        await until(() => root.querySelector('#group-form[data-id="default"]') !== null, 'the settings did not open');

        field(root, '#group-form', 'name').value = 'typed for default';

        root.querySelector<HTMLButtonElement>('.group-edit[data-id="field-test"]')!.click();
        await until(() => root.querySelector('#group-form[data-id="field-test"]') !== null, 'the other settings did not open');

        assert.equal(field(root, '#group-form', 'name').value, 'Group field-test', 'the other group\'s settings say what was typed into the first');

    });

    it('empty the form once a station is added, and say its password', async () => {

        const root = await opened();

        field(root, '#station-form', 'id').value   = 'cs003';
        field(root, '#station-form', 'note').value = 'Ladepunkt 3';
        change(field<HTMLSelectElement>(root, '#station-form', 'group'), 'field-test');

        submit(root, '#station-form');
        await until(() => root.querySelector('.station-enabled[data-id="cs003"]') !== null, 'the station added was not drawn');

        assert.deepEqual(asked.find(one => one.method === 'POST')?.body, { id: 'cs003', password: '', group: 'field-test', note: 'Ladepunkt 3' });
        assert.equal(root.querySelector('code.password')?.textContent, 'made up, shown once');
        assert.equal(field(root, '#station-form', 'id').value,                          '', 'the station added is still in the form');
        assert.equal(field(root, '#station-form', 'note').value,                        '');
        assert.equal(field<HTMLSelectElement>(root, '#station-form', 'group').value,    'default');

    });

});
