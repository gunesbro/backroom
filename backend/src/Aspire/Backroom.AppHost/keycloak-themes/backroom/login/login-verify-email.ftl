<#-- "Verify your email" of docs/design Signup (step 2), on keycloak.v2's login-verify-email.ftl (Keycloak 26.8.1): a mail
     icon, what happens next, a spam-folder note and a resend button. Compare with keycloak.v2 on Keycloak upgrades. -->
<#import "template.ftl" as layout>
<#import "buttons.ftl" as buttons>
<@layout.registrationLayout; section>
    <#if section = "icon">
        <span class="ph-status-icon" aria-hidden="true">
            <svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="5" width="18" height="14" rx="2"></rect><path d="M3 7l9 6 9-6"></path></svg>
        </span>
    <#elseif section = "header">
        ${msg("emailVerifyTitle")}
    <#elseif section = "form">
        <p class="ph-lead">
            <#if verifyEmail??>
                ${msg("emailVerifyInstruction1",verifyEmail)}
            <#else>
                ${msg("emailVerifyInstruction4",user.email)}
            </#if>
        </p>
        <#if isAppInitiatedAction??>
            <form id="kc-verify-email-form" class="${properties.kcFormClass!}" action="${url.loginAction}" method="post">
                <@buttons.actionGroup horizontal=true>
                    <#if verifyEmail??>
                        <@buttons.button id="kc-resend" label="emailVerifyResend" type="secondary"/>
                    <#else>
                        <@buttons.button id="kc-send" label="emailVerifySend"/>
                    </#if>
                    <@buttons.button id="kc-cancel" label="doCancel" type="secondary" name="cancel-aia" value="true"/>
                </@buttons.actionGroup>
            </form>
        <#else>
            <p class="ph-note">${msg("phCheckSpam")}</p>
            <a class="${properties.kcButtonSecondaryClass!} ${properties.kcButtonBlockClass!}" href="${url.loginAction}">${msg("phResend")}</a>
        </#if>
    </#if>
</@layout.registrationLayout>
