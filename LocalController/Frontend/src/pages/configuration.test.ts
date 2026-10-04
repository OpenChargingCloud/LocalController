/**
 * The configuration drawn, in a document of happy-dom, against a stand-in
 * controller: its cards - html.ts's, in a template of view.ts - stand as the
 * markup they are, and Reload draws them again with what the controller says
 * then.
 */

import { open, until, type Asked } from '../../test/controller.ts';

import { strict as assert }  from 'node:assert';
import { describe, it }      from 'node:test';

const { configurationPage } = await import('./configuration.ts');


let uptime = '1 minute';

function controller({ path }: Asked): unknown {

    if (path === '/status')
        return { service: 'LocalController', version: '1.0', hermod: null, timestamp: '2026-10-04T12:00:00Z',
                 startedAt: '2026-10-04T11:59:00Z', uptime, sessions: 1, log: { entries: 0, capacity: 100, lastId: 0, tags: [] },
                 ocppId: 'lc001' };

    if (path === '/configuration')
        return { controller: { ocppId: 'lc001' }, ocpp: { version: '2.1', role: 'local controller' },
                 http: { port: 8080 }, web: {}, log: {}, time: {},
                 assemblies: [ { name: 'LocalController', version: '1.0', commit: 'abc1234' } ] };

    return undefined;

}


describe('the configuration', () => {

    it('draws its cards as markup, and again on Reload', async () => {

        uptime = '1 minute';

        const root = await open(configurationPage, '/configuration', [ 'configuration:read' ],
                                controller, root => root.querySelector('.cards') !== null);

        assert.equal(root.querySelectorAll('.cards > section.card').length, 7);
        assert.match(root.querySelector('.cards')!.textContent!, /OCPP 2\.1 - local controller/);
        assert.match(root.querySelector('.cards')!.textContent!, /Uptime\s+1 minute/);
        assert.doesNotMatch(root.textContent!, /<section/, 'a card was taken as text');

        uptime = '2 minutes';
        root.querySelector<HTMLButtonElement>('#reload')!.click();

        await until(() => /Uptime\s+2 minutes/.test(root.textContent!), 'Reload did not draw what the controller says then');

        assert.equal(root.querySelectorAll('.cards > section.card').length, 7);

    });

});
