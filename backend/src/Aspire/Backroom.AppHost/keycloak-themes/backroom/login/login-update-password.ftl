<#-- Setting a password, on keycloak.v2's login-update-password.ftl (Keycloak 26.8.1). Two cases from docs/design:
     - a new account after verifying its email (Signup, step 3): "Set your password", other devices always signed out (no choice shown);
     - a password reset (ResetPassword, step 3): "Set a new password", signing out other devices checked.
     Both post to the same required-action URL. Keycloak shows updatePasswordMessage only when UPDATE_PASSWORD is stored
     on the user (a new account, or a change an administrator required), not after a reset; that picks the first
     variant, which always signs out other devices. After a validation error the message is the error, so the page
     falls back to the reset variant, whose sign-out checkbox is checked by default.
     Compare with keycloak.v2 on Keycloak upgrades. -->
<#import "template.ftl" as layout>
<#import "field.ftl" as field>
<#import "buttons.ftl" as buttons>
<#import "password-validation.ftl" as validator>
<#-- The required action's own "change your password to activate your account" is replaced by the lead text. -->
<#assign firstPassword = !isAppInitiatedAction?? && message?? && message.summary == msg("updatePasswordMessage")>
<@layout.registrationLayout displayMessage=!firstPassword && !messagesPerField.existsError('password','password-confirm'); section>
    <#if section = "icon">
        <#if firstPassword>
            <span class="ph-status-icon ph-status-icon--ok" aria-hidden="true">
                <svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"><path d="M5 12.5l4.5 4.5L19 7.5"></path></svg>
            </span>
        </#if>
    <#elseif section = "header">
        ${msg(firstPassword?then("phSetPasswordTitle", "phNewPasswordTitle"))}
    <#elseif section = "form">
        <#if firstPassword>
            <p class="ph-lead">${msg("phSetPasswordLead")}</p>
        </#if>
        <form id="kc-passwd-update-form" class="${properties.kcFormClass!}" onsubmit="login.disabled = true; return true;" action="${url.loginAction}" method="post" novalidate="novalidate">
            <@field.password name="password-new" label=msg(firstPassword?then("password", "passwordNew")) fieldName="password" autocomplete="new-password" autofocus=true />
            <@field.password name="password-confirm" label=msg(firstPassword?then("passwordConfirm", "phNewPasswordConfirm")) autocomplete="new-password" />

            <#if firstPassword>
                <#-- A new account has no other sessions; a required change (a suspected compromise) must end them. -->
                <input type="hidden" name="logout-sessions" value="on"/>
            <#else>
                <div class="${properties.kcFormGroupClass!}">
                    <div id="kc-form-options" class="${properties.kcFormOptionsClass!}">
                        <div class="${properties.kcFormOptionsWrapperClass!}">
                            <#-- Checked by default: after a reset, other devices should usually be signed out. -->
                            <@field.checkbox name="logout-sessions" label=msg("logoutOtherSessions") value=true />
                        </div>
                    </div>
                </div>
            </#if>

            <@buttons.actionGroup horizontal=true>
                <@buttons.button id="kc-submit" name="login" label=firstPassword?then("phSaveAndContinue", "phSavePassword")/>
                <#if isAppInitiatedAction??>
                    <@buttons.button id="kc-cancel" label="doCancel" name="cancel-aia" type="secondary"/>
                </#if>
            </@buttons.actionGroup>
        </form>

        <@validator.templates/>
        <@validator.script field="password-new"/>
    </#if>
</@layout.registrationLayout>
