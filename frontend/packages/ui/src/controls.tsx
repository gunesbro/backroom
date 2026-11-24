// Controls of the signed-in app shell (docs/design: Main, Thread, OrgNotif): controls for later phases, menus and
// toolbars. Texts come from the app; this package has no translations.
import {
  useCallback,
  useEffect,
  useId,
  useRef,
  useState,
  type KeyboardEvent,
  type MouseEvent,
  type ReactNode,
} from 'react';
import { Icon } from './icons';

function classes(...names: (string | false | undefined)[]): string {
  return names.filter(Boolean).join(' ');
}

export type TipPlacement = 'top' | 'bottom' | 'bottom-end' | 'top-start';

/**
 * A control the design shows for a later phase: visible and focusable, so people can find it, but marked disabled
 * (aria-disabled) and inert. A tooltip on hover and focus says it is coming; screen readers get that as its
 * description. It never runs an action.
 */
export function SoonButton({
  label,
  soon,
  children,
  className,
  placement = 'top',
  showLabel = false,
  tabbable = true,
}: {
  /** The control's name, e.g. "Emoji". */
  label: string;
  /** "Coming soon" in the person's language. */
  soon: string;
  /** The visible content; an icon when `showLabel` is false. */
  children: ReactNode;
  className?: string | undefined;
  placement?: TipPlacement;
  /** The content already shows the name, so it is not repeated as aria-label. */
  showLabel?: boolean;
  /** False keeps it out of the tab order (inside a bar hidden from assistive technology). */
  tabbable?: boolean;
}) {
  const tipId = useId();
  const inert = (event: MouseEvent) => {
    event.preventDefault();
  };
  return (
    <span className="ph-soon">
      <button
        type="button"
        className={className}
        aria-label={showLabel ? undefined : label}
        aria-disabled="true"
        aria-describedby={tipId}
        tabIndex={tabbable ? undefined : -1}
        onClick={inert}
      >
        {children}
      </button>
      <span className={`ph-tooltip ph-tooltip--${placement}`}>
        <span aria-hidden="true">{label} · </span>
        <span id={tipId}>{soon}</span>
      </span>
    </span>
  );
}

/**
 * A row of buttons with one tab stop (WAI-ARIA toolbar): the arrow keys, Home and End move between them.
 */
export function Toolbar({
  label,
  className,
  children,
}: {
  label: string;
  className?: string | undefined;
  children: ReactNode;
}) {
  const ref = useRef<HTMLDivElement>(null);
  const buttons = () => Array.from(ref.current?.querySelectorAll<HTMLButtonElement>('button') ?? []);
  const rove = (current: HTMLElement | undefined) => {
    for (const button of buttons()) button.tabIndex = button === current ? 0 : -1;
  };

  useEffect(() => {
    const all = Array.from(ref.current?.querySelectorAll<HTMLButtonElement>('button') ?? []);
    for (const [index, button] of all.entries()) button.tabIndex = index === 0 ? 0 : -1;
  }, []);

  const keyDown = (event: KeyboardEvent<HTMLDivElement>) => {
    const items = buttons();
    const index = items.findIndex((item) => item === document.activeElement);
    if (index < 0) return;
    const next =
      event.key === 'ArrowRight'
        ? items[(index + 1) % items.length]
        : event.key === 'ArrowLeft'
          ? items[(index - 1 + items.length) % items.length]
          : event.key === 'Home'
            ? items[0]
            : event.key === 'End'
              ? items.at(-1)
              : undefined;
    if (!next) return;
    event.preventDefault();
    rove(next);
    next.focus();
  };

  return (
    <div
      ref={ref}
      role="toolbar"
      aria-label={label}
      className={className}
      onKeyDown={keyDown}
      onFocus={(event) => {
        if (event.target instanceof HTMLButtonElement) rove(event.target);
      }}
    >
      {children}
    </div>
  );
}

export type MenuEntry =
  | {
      kind: 'item';
      id: string;
      label: ReactNode;
      /** A second line or a note at the end of the row. */
      detail?: ReactNode;
      icon?: ReactNode;
      /** The current choice of a list (an organization); shown with a check. */
      checked?: boolean;
      /** A later phase: shown, focusable, disabled and described with this text; does nothing. */
      soon?: string;
      onSelect?: () => void;
      /** Danger style (sign out, leave). */
      danger?: boolean;
    }
  | { kind: 'separator'; id: string }
  | { kind: 'group'; id: string; label: string; entries: readonly MenuEntry[] };

/**
 * A button that opens a menu (WAI-ARIA menu button): Enter, Space or the down arrow open it on the first item; the
 * arrows, Home and End move; Escape closes it and returns focus; Tab or a click outside closes it.
 */
export function MenuButton({
  label,
  button,
  buttonClassName,
  menuClassName,
  header,
  entries,
  placement = 'below',
}: {
  /** The button's accessible name; it must contain the button's visible text. */
  label: string;
  button: ReactNode;
  buttonClassName?: string | undefined;
  menuClassName?: string | undefined;
  /** Shown above the items, not part of the menu (the signed-in account, for example). */
  header?: ReactNode;
  entries: readonly MenuEntry[];
  placement?: 'below' | 'above';
}) {
  const menuId = useId();
  const [open, setOpen] = useState(false);
  const wrapperRef = useRef<HTMLDivElement>(null);
  const buttonRef = useRef<HTMLButtonElement>(null);
  const menuRef = useRef<HTMLDivElement>(null);

  const items = () => Array.from(menuRef.current?.querySelectorAll<HTMLElement>('[role^="menuitem"]') ?? []);

  const close = useCallback((returnFocus: boolean) => {
    setOpen(false);
    if (returnFocus) buttonRef.current?.focus();
  }, []);

  useEffect(() => {
    if (!open) return;
    items()[0]?.focus();
    const outside = (event: PointerEvent) => {
      if (event.target instanceof Node && !wrapperRef.current?.contains(event.target)) close(false);
    };
    document.addEventListener('pointerdown', outside);
    return () => {
      document.removeEventListener('pointerdown', outside);
    };
  }, [open, close]);

  const menuKeyDown = (event: KeyboardEvent<HTMLDivElement>) => {
    if (event.key === 'Escape') {
      event.preventDefault();
      event.stopPropagation();
      close(true);
      return;
    }

    if (event.key === 'Tab') {
      close(false);
      return;
    }

    const list = items();
    const index = list.findIndex((item) => item === document.activeElement);
    const next =
      event.key === 'ArrowDown'
        ? list[(index + 1) % list.length]
        : event.key === 'ArrowUp'
          ? list[(index - 1 + list.length) % list.length]
          : event.key === 'Home'
            ? list[0]
            : event.key === 'End'
              ? list.at(-1)
              : undefined;
    if (next) {
      event.preventDefault();
      next.focus();
    }
  };

  const render = (list: readonly MenuEntry[]): ReactNode =>
    list.map((entry) => {
      if (entry.kind === 'separator') return <div key={entry.id} role="separator" className="ph-menu__sep" />;
      if (entry.kind === 'group') {
        const labelId = `${menuId}-${entry.id}`;
        return (
          <div key={entry.id} role="group" aria-labelledby={labelId}>
            <div id={labelId} className="ph-menu__group">
              {entry.label}
            </div>
            {render(entry.entries)}
          </div>
        );
      }

      const soonId = entry.soon ? `${menuId}-${entry.id}-soon` : undefined;
      return (
        <div
          key={entry.id}
          role={entry.checked === undefined ? 'menuitem' : 'menuitemradio'}
          aria-checked={entry.checked}
          aria-disabled={entry.soon ? true : undefined}
          aria-describedby={soonId}
          tabIndex={-1}
          className={classes(
            'ph-menu__item',
            entry.checked && 'ph-menu__item--checked',
            entry.danger && 'ph-menu__item--danger',
          )}
          onClick={() => {
            if (entry.soon) return;
            close(true);
            entry.onSelect?.();
          }}
          onKeyDown={(event) => {
            if (event.key !== 'Enter' && event.key !== ' ') return;
            event.preventDefault();
            event.currentTarget.click();
          }}
        >
          {entry.icon && <span className="ph-menu__icon">{entry.icon}</span>}
          <span className="ph-menu__label">
            {entry.label}
            {entry.detail && <small>{entry.detail}</small>}
          </span>
          {entry.soon && (
            <span id={soonId} className="ph-menu__soon" aria-hidden="true">
              {entry.soon}
            </span>
          )}
          {entry.checked && (
            <span className="ph-menu__check">
              <Icon name="check" size={16} />
            </span>
          )}
        </div>
      );
    });

  return (
    <div ref={wrapperRef} className="ph-menu-anchor">
      <button
        ref={buttonRef}
        type="button"
        className={buttonClassName}
        aria-label={label}
        aria-haspopup="menu"
        aria-expanded={open}
        aria-controls={open ? menuId : undefined}
        onClick={() => {
          setOpen((value) => !value);
        }}
        onKeyDown={(event) => {
          if (event.key === 'ArrowDown' && !open) {
            event.preventDefault();
            setOpen(true);
          }
        }}
      >
        {button}
      </button>
      {open && (
        <div className={classes('ph-menu', `ph-menu--${placement}`, menuClassName)}>
          {header}
          <div id={menuId} ref={menuRef} role="menu" aria-label={label} onKeyDown={menuKeyDown}>
            {render(entries)}
          </div>
        </div>
      )}
    </div>
  );
}

const FOCUSABLE =
  'button:not([disabled]):not([tabindex="-1"]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [href], [tabindex]:not([tabindex="-1"])';

/**
 * A modal dialog (docs/design: Dialogs): a title with a close button over the content. Focus moves to
 * `initialFocus` (or the dialog) when it opens, stays inside while it is open, and returns to what opened it; Escape
 * closes it. The content lays out its body and footer with the ph-dialog__body and ph-dialog__foot classes.
 */
export function Dialog({
  title,
  closeLabel,
  onClose,
  initialFocus,
  children,
  wide = false,
}: {
  title: ReactNode;
  closeLabel: string;
  onClose: () => void;
  initialFocus?: { readonly current: HTMLElement | null } | undefined;
  children: ReactNode;
  /** 520 px instead of 420 px, for a dialog with a list. */
  wide?: boolean;
}) {
  const titleId = useId();
  const dialogRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const opener = document.activeElement;
    (initialFocus?.current ?? dialogRef.current)?.focus();
    return () => {
      if (opener instanceof HTMLElement) opener.focus();
    };
    // Only when the dialog opens.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const keyDown = (event: KeyboardEvent<HTMLDivElement>) => {
    if (event.key === 'Escape') {
      event.stopPropagation();
      onClose();
      return;
    }

    if (event.key !== 'Tab') return;
    const items = dialogRef.current?.querySelectorAll<HTMLElement>(FOCUSABLE);
    const first = items?.[0];
    const last = items?.[items.length - 1];
    if (event.shiftKey && document.activeElement === first) {
      event.preventDefault();
      last?.focus();
    } else if (!event.shiftKey && document.activeElement === last) {
      event.preventDefault();
      first?.focus();
    }
  };

  return (
    <div className="ph-backdrop">
      <div
        ref={dialogRef}
        className={classes('ph-dialog', wide && 'ph-dialog--wide')}
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        tabIndex={-1}
        onKeyDown={keyDown}
      >
        <div className="ph-dialog__head">
          <h2 id={titleId} className="ph-dialog__title">
            {title}
          </h2>
          <button type="button" className="ph-dialog__close" aria-label={closeLabel} onClick={onClose}>
            <Icon name="close" size={18} />
          </button>
        </div>
        {children}
      </div>
    </div>
  );
}
