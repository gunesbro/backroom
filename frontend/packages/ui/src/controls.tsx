// Controls of the signed-in app shell (docs/design: Main, Thread, OrgNotif): controls for later phases, menus and
// toolbars. Texts come from the app; this package has no translations.
import {
  useCallback,
  useEffect,
  useId,
  useLayoutEffect,
  useRef,
  useState,
  type CSSProperties,
  type KeyboardEvent,
  type MouseEvent,
  type SyntheticEvent,
  type ReactNode,
} from 'react';
import { createPortal } from 'react-dom';
import { Icon } from './icons';

function classes(...names: (string | false | undefined)[]): string {
  return names.filter(Boolean).join(' ');
}

export type TipPlacement = 'top' | 'bottom' | 'bottom-end' | 'top-start';

/** Where a tooltip goes for its control's box, in viewport coordinates (it is drawn with position: fixed). */
function tipPosition(rect: DOMRect, placement: TipPlacement): CSSProperties {
  const gap = 6;
  switch (placement) {
    case 'top':
      return { left: rect.left + rect.width / 2, top: rect.top - gap, transform: 'translate(-50%, -100%)' };
    case 'top-start':
      return { left: rect.left, top: rect.top - gap, transform: 'translateY(-100%)' };
    case 'bottom':
      return { left: rect.left + rect.width / 2, top: rect.bottom + gap, transform: 'translateX(-50%)' };
    case 'bottom-end':
      return { left: rect.right, top: rect.bottom + gap, transform: 'translateX(-100%)' };
  }
}

/**
 * A control the design shows for a later phase: visible and focusable, so people can find it, but marked disabled
 * (aria-disabled) and inert. A tooltip (role="tooltip") on hover and keyboard focus says it is coming; screen readers
 * get that as its description. The tooltip is rendered into the page body, so a scrolling sidebar or list never
 * cuts it off. It never runs an action.
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
  const [position, setPosition] = useState<CSSProperties | undefined>();
  const show = (target: HTMLElement) => {
    setPosition(tipPosition(target.getBoundingClientRect(), placement));
  };
  const hide = () => {
    setPosition(undefined);
  };
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
        onMouseEnter={(event) => {
          show(event.currentTarget);
        }}
        onMouseLeave={hide}
        onFocus={(event) => {
          if (event.currentTarget.matches(':focus-visible')) show(event.currentTarget);
        }}
        onBlur={hide}
        onKeyDown={(event) => {
          if (event.key === 'Escape' && position) {
            event.preventDefault();
            event.stopPropagation();
            hide();
          }
        }}
      >
        {children}
      </button>
      {createPortal(
        <span className="ph-tooltip" role="tooltip" style={position} hidden={!position}>
          <span aria-hidden="true">{label} · </span>
          <span id={tipId}>{soon}</span>
        </span>,
        document.body,
      )}
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
      /** Shown and described, but cannot be chosen (an automatic state, for example); its detail says why. */
      disabled?: boolean;
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
  description,
  button,
  buttonClassName,
  menuClassName,
  header,
  entries,
  placement = 'below',
}: {
  /** The button's accessible name, e.g. "Switch organization". */
  label: string;
  /** Read after the name, e.g. the current organization the button shows. */
  description?: string | undefined;
  button: ReactNode;
  buttonClassName?: string | undefined;
  menuClassName?: string | undefined;
  /** Shown above the items, not part of the menu (the signed-in account, for example). */
  header?: ReactNode;
  entries: readonly MenuEntry[];
  placement?: 'below' | 'above';
}) {
  const menuId = useId();
  const descriptionId = useId();
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
          aria-disabled={entry.soon || entry.disabled ? true : undefined}
          aria-describedby={soonId}
          tabIndex={-1}
          className={classes(
            'ph-menu__item',
            entry.checked && 'ph-menu__item--checked',
            entry.danger && 'ph-menu__item--danger',
          )}
          onClick={() => {
            if (entry.soon || entry.disabled) return;
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
        aria-describedby={description ? descriptionId : undefined}
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
      {description && (
        <span id={descriptionId} hidden>
          {description}
        </span>
      )}
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

/**
 * A modal dialog (docs/design: Dialogs): a title with a close button over the content. Focus moves to
 * `initialFocus` (or the dialog) when it opens, stays inside while it is open (the page behind is inert), and returns to what opened it; Escape
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
  const dialogRef = useRef<HTMLDialogElement>(null);

  // The native modal makes the page inert and keeps focus inside; React owns the lifecycle, so the dialog opens on
  // mount and is closed only by unmounting.
  useLayoutEffect(() => {
    const dialog = dialogRef.current;
    const opener = document.activeElement;
    if (dialog && !dialog.open) dialog.showModal();
    (initialFocus?.current ?? dialog)?.focus();
    return () => {
      if (dialog?.open) dialog.close();
      if (opener instanceof HTMLElement) opener.focus();
    };
    // Only when the dialog opens.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const keyDown = (event: KeyboardEvent<HTMLDialogElement>) => {
    if (event.key !== 'Escape') return;
    // preventDefault stops the browser's own cancel; an inner control that used Escape has already done the same.
    event.preventDefault();
    event.stopPropagation();
    onClose();
  };

  // A close request that is not a keydown, such as the back gesture.
  const cancel = (event: SyntheticEvent<HTMLDialogElement>) => {
    event.preventDefault();
    onClose();
  };

  return (
    <dialog
      ref={dialogRef}
      className={classes('ph-dialog', wide && 'ph-dialog--wide')}
      aria-labelledby={titleId}
      tabIndex={-1}
      onKeyDown={keyDown}
      onCancel={cancel}
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
    </dialog>
  );
}
