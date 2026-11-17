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

export * from '@node/../test/node.ts';

standIn({ name: 'Local Controller', icon: 'fa-sitemap' });
