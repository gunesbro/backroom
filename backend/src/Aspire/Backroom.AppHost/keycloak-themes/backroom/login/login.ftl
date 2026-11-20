<#-- The sign-in page of docs/design Login, on keycloak.v2's login.ftl (Keycloak 26.8.1): lead text, external sign-in
     first with an "or with email" divider, one error box for wrong credentials, and the forgot-password link beside the
     password label. Compare with keycloak.v2 on Keycloak upgrades. -->
<#import "template.ftl" as layout>
<#import "field.ftl" as field>
<#import "buttons.ftl" as buttons>
<#import "passkeys.ftl" as passkeys>
<#assign credentialsError = messagesPerField.existsError('username','password')>
<@layout.registrationLayout displayMessage=!credentialsError displayInfo=realm.password && realm.registrationAllowed && !registrationDisabled??; section>
    <#if section = "header">
        ${msg("loginAccountTitle")}
    <#elseif section = "form">
        <p class="ph-lead">${msg("phLoginLead")}</p>
        <#if realm.password && social.providers?? && social.providers?has_content>
            <div class="ph-external">
                <#list social.providers as p>
                    <a id="social-${p.alias}" class="${properties.kcButtonSecondaryClass!} ${properties.kcButtonBlockClass!}"
                       href="${p.loginUrl}" data-once-link>${msg("phContinueWith", p.displayName!)}</a>
                </#list>
            </div>
            <div class="ph-or">${msg("phOrEmail")}</div>
        </#if>
        <#if credentialsError>
            <#-- Both fields point here: the error is about the pair, and it is already on the page when it loads. -->
            <div class="ph-error" id="credentials-error" role="alert">${kcSanitize(messagesPerField.getFirstError('username','password'))?no_esc}</div>
        </#if>
        <div id="kc-form">
          <div id="kc-form-wrapper">
            <#if realm.password>
                <form id="kc-form-login" class="${properties.kcFormClass!}" onsubmit="login.disabled = true; return true;" action="${url.loginAction}" method="post" novalidate="novalidate">
                    <#if !usernameHidden??>
                        <#assign label>
                            <#if !realm.loginWithEmailAllowed>${msg("username")}<#elseif !realm.registrationEmailAsUsername>${msg("usernameOrEmail")}<#else>${msg("email")}</#if>
                        </#assign>
                        <div class="${properties.kcFormGroupClass!}">
                            <div class="${properties.kcFormGroupLabelClass!}">
                                <label for="username" class="${properties.kcFormLabelClass!}"><span class="${properties.kcFormLabelTextClass!}">${label}</span></label>
                            </div>
                            <span class="${properties.kcInputClass!} <#if credentialsError>${properties.kcError}</#if>">
                                <input id="username" name="username" value="${login.username!''}" type="text"
                                       autocomplete="${(enableWebAuthnConditionalUI?has_content)?then('username webauthn', 'username')}"
                                       <#if !credentialsError>autofocus</#if>
                                       <#if credentialsError>aria-invalid="true" aria-describedby="credentials-error"</#if>/>
                            </span>
                        </div>
                    </#if>

                    <#-- The forgot-password link follows the field in the DOM (so Tab reaches the field first) and is
                         placed beside the label by CSS, as in the design. -->
                    <div class="${properties.kcFormGroupClass!} ph-password-group">
                        <label for="password" class="${properties.kcFormLabelClass!} ph-password-label"><span class="${properties.kcFormLabelTextClass!}">${msg("password")}</span></label>
                        <div class="${properties.kcInputGroup} ph-password-input">
                          <div class="${properties.kcInputGroupItemClass} ${properties.kcFill}">
                            <span class="${properties.kcInputClass} <#if credentialsError>${properties.kcError}</#if>">
                              <input id="password" name="password" type="password" autocomplete="current-password"
                                     <#if usernameHidden?? || credentialsError>autofocus</#if>
                                     <#if credentialsError>aria-invalid="true" aria-describedby="credentials-error"</#if>/>
                            </span>
                          </div>
                          <div class="${properties.kcInputGroupItemClass}">
                            <button class="${properties.kcFormPasswordVisibilityButtonClass}" type="button" aria-label="${msg('showPassword')}"
                                    aria-controls="password" data-password-toggle
                                    data-icon-show="${properties.kcFormPasswordVisibilityIconShow}" data-icon-hide="${properties.kcFormPasswordVisibilityIconHide}"
                                    data-label-show="${msg('showPassword')}" data-label-hide="${msg('hidePassword')}" id="password-show-password">
                                <i class="${properties.kcFormPasswordVisibilityIconShow}" aria-hidden="true"></i>
                            </button>
                          </div>
                        </div>
                        <#if realm.resetPasswordAllowed>
                            <a class="ph-password-forgot" href="${url.loginResetCredentialsUrl}">${msg("doForgotPassword")}</a>
                        </#if>
                    </div>

                    <#if realm.rememberMe && !usernameHidden??>
                        <@field.checkbox name="rememberMe" label=msg("rememberMe") value=login.rememberMe?? />
                    </#if>

                    <input type="hidden" id="id-hidden-input" name="credentialId" <#if auth.selectedCredential?has_content>value="${auth.selectedCredential}"</#if>/>
                    <@buttons.loginButton />
                </form>
            </#if>
            </div>
        </div>
        <@passkeys.conditionalUIData />
    <#elseif section = "info" >
        <#if realm.password && realm.registrationAllowed && !registrationDisabled??>
            <div id="kc-registration-container">
                <div id="kc-registration">
                    <span>${msg("noAccount")} <a href="${url.registrationUrl}">${msg("doRegister")}</a></span>
                </div>
            </div>
        </#if>
    </#if>
</@layout.registrationLayout>
