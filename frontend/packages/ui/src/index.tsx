// Design tokens and shared components (docs/design: the design canvas). Public surface of @backroom/ui.
import {
  useEffect,
  useId,
  useRef,
  type ButtonHTMLAttributes,
  type InputHTMLAttributes,
  type ReactNode,
} from 'react';
import { initials } from './initials';
import './styles.css';

function classes(...names: (string | false | undefined)[]): string {
  return names.filter(Boolean).join(' ');
}

/** A centered page with a column of cards. */
export function Page({ children }: { children: ReactNode }) {
  return (
    <main className="ph-page">
      <div className="ph-stack">{children}</div>
    </main>
  );
}

/** A panel; narrow (360 px) for a single task, medium (424 px) for one card of a pair. */
export function Card({
  children,
  narrow = false,
  medium = false,
  labelledBy,
}: {
  children: ReactNode;
  narrow?: boolean;
  medium?: boolean;
  labelledBy?: string | undefined;
}) {
  return (
    <section
      className={classes('ph-card', narrow && 'ph-card--narrow', medium && 'ph-card--medium')}
      aria-labelledby={labelledBy}
    >
      {children}
    </section>
  );
}

/** The product's mark and name; the name comes from the single branding setting. */
export function Brand({ name }: { name: string }) {
  return (
    <div className="ph-brand">
      <span className="ph-brand__logo" aria-hidden="true" />
      {name}
    </div>
  );
}

/** A level 1 heading takes focus when it appears, so a new screen is announced and keyboard focus is not lost. */
export function Heading({ id, children, level = 1 }: { id?: string; children: ReactNode; level?: 1 | 2 }) {
  const ref = useRef<HTMLHeadingElement>(null);
  useEffect(() => {
    if (level === 1) ref.current?.focus({ preventScroll: true });
  }, [level]);

  return level === 1 ? (
    <h1 ref={ref} id={id} className="ph-heading" tabIndex={-1}>
      {children}
    </h1>
  ) : (
    <h2 id={id} className="ph-subheading">
      {children}
    </h2>
  );
}

export function Muted({ children }: { children: ReactNode }) {
  return <p className="ph-muted">{children}</p>;
}

export function Button({
  variant = 'secondary',
  className,
  ...props
}: ButtonHTMLAttributes<HTMLButtonElement> & { variant?: 'primary' | 'secondary' }) {
  return (
    <button
      type="button"
      {...props}
      className={classes('ph-button', variant === 'primary' && 'ph-button--primary', className)}
    />
  );
}

/** A button that reads as a link, for secondary actions such as signing out. */
export function LinkButton(props: ButtonHTMLAttributes<HTMLButtonElement>) {
  return <button type="button" {...props} className="ph-link" />;
}

type FieldProps = { label: string; hint?: string | undefined; error?: string | undefined };

export function TextField({
  label,
  hint,
  error,
  ...input
}: FieldProps & InputHTMLAttributes<HTMLInputElement>) {
  const id = useId();
  const described = [hint && `${id}-hint`, error && `${id}-error`].filter(Boolean).join(' ') || undefined;
  return (
    <div className="ph-field">
      <label className="ph-field__label" htmlFor={id}>
        {label}
      </label>
      <input
        {...input}
        id={id}
        className="ph-input"
        aria-invalid={error ? true : undefined}
        aria-describedby={described}
      />
      {hint && (
        <span id={`${id}-hint`} className="ph-field__hint">
          {hint}
        </span>
      )}
      {error && (
        <span id={`${id}-error`} className="ph-field__error" role="alert">
          {error}
        </span>
      )}
    </div>
  );
}

export function Note({ children }: { children: ReactNode }) {
  return <div className="ph-note">{children}</div>;
}

/**
 * An error for the whole form or page, announced to screen readers. A screen whose heading is the error passes
 * announce={false}: the heading takes focus and is read once.
 */
export function Alert({ children, announce = true }: { children: ReactNode; announce?: boolean }) {
  return (
    <div className="ph-alert" role={announce ? 'alert' : undefined}>
      {children}
    </div>
  );
}

/** Decorative; the text next to it says what is happening (inside a Status). */
export function Spinner() {
  return <span className="ph-spinner" aria-hidden="true" />;
}

/** Progress that screen readers announce politely. */
export function Status({ children }: { children: ReactNode }) {
  return (
    <div role="status" className="ph-status">
      {children}
    </div>
  );
}

/** A list of choices; each one is a button. */
export function ChoiceList({
  items,
  onChoose,
}: {
  items: readonly { id: string; label: string }[];
  onChoose: (id: string) => void;
}) {
  return (
    <ul className="ph-list">
      {items.map((item) => (
        <li key={item.id}>
          <button
            type="button"
            className="ph-list__item"
            onClick={() => {
              onChoose(item.id);
            }}
          >
            <span className="ph-avatar" aria-hidden="true">
              {initials(item.label)}
            </span>
            {item.label}
          </button>
        </li>
      ))}
    </ul>
  );
}

/** A page with a top bar (product and account) above centered content; for the signed-in screens. */
export function AppPage({
  start,
  end,
  children,
}: {
  start: ReactNode;
  end?: ReactNode;
  children: ReactNode;
}) {
  return (
    <div className="ph-app-page">
      <header className="ph-topbar">
        {start}
        {end}
      </header>
      <main className="ph-app-page__main">{children}</main>
    </div>
  );
}

/** The signed-in person in the top bar: initials, name and actions such as signing out. */
export function Account({ name, children }: { name: string; children?: ReactNode }) {
  return (
    <div className="ph-account">
      <span className="ph-avatar ph-avatar--person" aria-hidden="true">
        {initials(name)}
      </span>
      <span className="ph-account__name">{name}</span>
      {children}
    </div>
  );
}

/** The initials of a person in a round avatar; decorative, the name is always written next to it. */
export function Avatar({ name }: { name: string }) {
  return (
    <span className="ph-avatar ph-avatar--person" aria-hidden="true">
      {initials(name)}
    </span>
  );
}

/** A bordered row naming an account: initials, name and a line below (docs/design: InviteAccept). */
export function AccountRow({ name, detail }: { name: string; detail: ReactNode }) {
  return (
    <div className="ph-account-row">
      <span className="ph-avatar ph-avatar--person" aria-hidden="true">
        {initials(name)}
      </span>
      <span className="ph-account-row__text">
        <b>{name}</b>
        <small>{detail}</small>
      </span>
    </div>
  );
}

/** A large page title, centered, with an optional lead text. */
export function PageTitle({ children, lead }: { children: ReactNode; lead?: ReactNode }) {
  return (
    <div className="ph-page-title">
      <Heading>{children}</Heading>
      {lead && <p className="ph-muted">{lead}</p>}
    </div>
  );
}

type RadioOption = { value: string; label: string; description?: string | undefined };

/** A choice among a few options, each a bordered row with a label and a description. */
export function RadioCards({
  legend,
  name,
  options,
  value,
  onChange,
}: {
  legend: string;
  name: string;
  options: readonly RadioOption[];
  value: string;
  onChange: (value: string) => void;
}) {
  return (
    <fieldset className="ph-radio-cards">
      <legend className="ph-field__label">{legend}</legend>
      {options.map((option) => (
        <label key={option.value} className="ph-radio-card">
          <input
            type="radio"
            name={name}
            value={option.value}
            checked={option.value === value}
            onChange={() => {
              onChange(option.value);
            }}
          />
          <span>
            <b>{option.label}</b>
            {option.description && <small>{option.description}</small>}
          </span>
        </label>
      ))}
    </fieldset>
  );
}

/** Cards side by side on wide screens, stacked on narrow ones. */
export function Pair({ children }: { children: ReactNode }) {
  return <div className="ph-pair">{children}</div>;
}
