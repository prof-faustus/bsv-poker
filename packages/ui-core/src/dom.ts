/**
 * dom.ts — the project's OWN tiny, dependency-free DOM toolkit. STANDALONE: it replaces the React +
 * react-dom dependency with ~80 lines of explicit, auditable code. There is NO virtual DOM, NO
 * reconciliation, NO hidden lifecycle — `el(...)` builds a real DOM node and `mount(...)` re-renders
 * a subtree when the store changes by replacing it. Every behaviour is visible here.
 *
 * WHY framework-free: this is reference infrastructure that must not rely on external code. A UI
 * framework is a large external dependency whose internals an auditor cannot see; this toolkit is a
 * handful of functions over the standard DOM API (the browser platform), so the whole view layer is
 * in-tree and reviewable.
 *
 * SECURITY: text is set via `textContent` (never `innerHTML`), so user/relay-derived strings cannot
 * inject markup (no XSS via the view layer). Attributes are set with `setAttribute`; event handlers
 * are real `addEventListener` calls. The view layer holds no load-bearing state (REQ-UI-002) — it
 * renders a snapshot the app-services layer produces and dispatches the human's explicit actions.
 */

/** A child of an element: a DOM node, a string (becomes a text node), or null/false (skipped). */
export type Child = Node | string | number | null | false | undefined;

/** Props for {@link el}: attributes, `class`, `style` object, `on<Event>` handlers, and `value`. */
export interface Props {
  readonly [key: string]: unknown;
}

/**
 * Create a DOM element. `props.class` sets className; `props.style` (an object) sets inline styles;
 * any `onClick`/`onInput`/… key adds the corresponding lowercased event listener; `value`/`checked`/
 * `disabled` set the property; everything else is a string attribute. Children are appended in order.
 */
export function el(tag: string, props: Props = {}, ...children: Child[]): HTMLElement {
  const node = document.createElement(tag);
  for (const [k, v] of Object.entries(props)) {
    if (v === null || v === undefined || v === false) continue;
    if (k === 'class' || k === 'className') node.className = String(v);
    else if (k === 'style' && typeof v === 'object') Object.assign(node.style, v as Record<string, string>);
    else if (k === 'value') (node as HTMLInputElement).value = String(v);
    else if (k === 'checked') (node as HTMLInputElement).checked = Boolean(v);
    else if (k === 'disabled') (node as HTMLButtonElement).disabled = Boolean(v);
    else if (k.startsWith('on') && typeof v === 'function') node.addEventListener(k.slice(2).toLowerCase(), v as EventListener);
    else node.setAttribute(k, String(v));
  }
  for (const c of children) appendChild(node, c);
  return node;
}

/** Append a child (node, text, or skip nullish/false) to a parent. Text uses textContent semantics. */
export function appendChild(parent: Node, c: Child): void {
  if (c === null || c === undefined || c === false) return;
  if (typeof c === 'string' || typeof c === 'number') parent.appendChild(document.createTextNode(String(c)));
  else parent.appendChild(c);
}

/** Convenience: a text node (explicit, for places that pass children arrays). */
export function text(s: string | number): Text {
  return document.createTextNode(String(s));
}

/** Replace every child of `root` with `child` (the unit of re-render). */
export function replaceChildren(root: HTMLElement, child: Child): void {
  while (root.firstChild) root.removeChild(root.firstChild);
  appendChild(root, child);
}

/** A store that holds a render snapshot and notifies on change (mirrors ./store). */
export interface Subscribable {
  subscribe(listener: () => void): () => void;
}

/**
 * Mount a reactive view: render once into `root`, then re-render (replace `root`'s subtree) whenever
 * `store` notifies. Returns an unmount function. No diffing — a full subtree replace per change; the
 * poker UI is small and this keeps the model trivially correct and auditable.
 */
export function mount(root: HTMLElement, render: () => HTMLElement, store: Subscribable): () => void {
  const update = (): void => replaceChildren(root, render());
  update();
  return store.subscribe(update);
}
