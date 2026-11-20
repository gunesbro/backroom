<#-- "Reset your password" of docs/design ResetPassword (step 1), on keycloak.v2's login-reset-password.ftl
     (Keycloak 26.8.1): the instruction above the field, "Send link", and a plain link back to sign-in.
     Compare with keycloak.v2 on Keycloak upgrades. -->
<#import "template.ftl" as layout>
<#import "field.ftl" as field>
<#import "buttons.ftl" as buttons>
<@layout.registrationLayout displayMessage=!messagesPerField.existsError('username'); section>
    <#if section = "header">
        ${msg("emailForgotTitle")}
    <#elseif section = "form">
        <p class="ph-lead">${msg("emailInstruction")}</p>
        <form id="kc-reset-password-form" class="${properties.kcFormClass!}" action="${url.loginAction}" method="post">
            <#assign label>
                <#if !realm.loginWithEmailAllowed>${msg("username")}<#elseif !realm.registrationEmailAsUsername>${msg("usernameOrEmail")}<#else>${msg("email")}</#if>
            </#assign>
            <@field.input name="username" label=label value=auth.attemptedUsername!'' autofocus=true autocomplete="email" />

            <@buttons.actionGroup>
              <@buttons.button id="kc-form-buttons" label="phSendLink"/>
            </@buttons.actionGroup>
        </form>
        <p class="ph-center"><a href="${url.loginUrl}">${msg("phBackToSignIn")}</a></p>
    </#if>
</@layout.registrationLayout>
