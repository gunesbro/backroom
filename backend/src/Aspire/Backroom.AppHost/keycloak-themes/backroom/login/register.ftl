<#-- The sign-up page of docs/design Signup, on keycloak.v2's register.ftl (Keycloak 26.8.1): external sign-up first with
     an "or with email" divider, no required-field legend, and "Already have an account? Sign in" below. The password is
     set after the email is verified (Keycloak's default when the realm verifies email), so the form has no password.
     Compare with keycloak.v2 on Keycloak upgrades. -->
<#import "template.ftl" as layout>
<#import "field.ftl" as field>
<#import "user-profile-commons.ftl" as userProfileCommons>
<#import "register-commons.ftl" as registerCommons>
<#import "password-validation.ftl" as validator>
<@layout.registrationLayout displayMessage=messagesPerField.exists('global'); section>
    <#if section = "header">
        <#if messageHeader??>
            ${msg(messageHeader)}
        <#else>
            ${msg("registerTitle")}
        </#if>
    <#elseif section = "form">
        <#if social.providers?? && social.providers?has_content>
            <div class="ph-external">
                <#list social.providers as p>
                    <a id="social-${p.alias}" class="${properties.kcButtonSecondaryClass!} ${properties.kcButtonBlockClass!}"
                       href="${p.loginUrl}" data-once-link>${msg("phContinueWith", p.displayName!)}</a>
                </#list>
            </div>
            <div class="ph-or">${msg("phOrEmail")}</div>
        </#if>
        <form id="kc-register-form" class="${properties.kcFormClass!}" action="${url.registrationAction}" method="post" novalidate="novalidate">
            <@userProfileCommons.userProfileFormFields; callback, attribute>
                <#if callback = "afterField">
                    <#if passwordRequired?? && (attribute.name == 'username' || (attribute.name == 'email' && realm.registrationEmailAsUsername))>
                        <@field.password name="password" required=true label=msg("password") autocomplete="new-password" />
                        <@field.password name="password-confirm" required=true label=msg("passwordConfirm") autocomplete="new-password" />
                    </#if>
                </#if>
            </@userProfileCommons.userProfileFormFields>

            <@registerCommons.termsAcceptance/>

            <#if recaptchaRequired?? && (recaptchaVisible!false)>
                <div class="form-group">
                    <div class="${properties.kcInputWrapperClass!}">
                        <div class="g-recaptcha" data-size="compact" data-sitekey="${recaptchaSiteKey}" data-action="${recaptchaAction}"></div>
                    </div>
                </div>
            </#if>

            <#if recaptchaRequired?? && !(recaptchaVisible!false)>
                <script>
                    function onSubmitRecaptcha(token) {
                        document.getElementById("kc-register-form").requestSubmit();
                    }
                </script>
                <div id="kc-form-buttons" class="${properties.kcFormButtonsClass!}">
                    <button class="${properties.kcButtonClass!} ${properties.kcButtonPrimaryClass!} ${properties.kcButtonBlockClass!} ${properties.kcButtonLargeClass!} g-recaptcha"
                            data-sitekey="${recaptchaSiteKey}" data-callback="onSubmitRecaptcha" data-action="${recaptchaAction}" type="submit" id="kc-submit">
                        ${msg("doRegister")}
                    </button>
                </div>
            <#else>
                <div id="kc-form-buttons" class="${properties.kcFormButtonsClass!}">
                    <input class="${properties.kcButtonClass!} ${properties.kcButtonPrimaryClass!} ${properties.kcButtonBlockClass!} ${properties.kcButtonLargeClass!}" type="submit" value="${msg("doRegister")}"/>
                </div>
            </#if>
        </form>
        <p class="ph-center">${msg("phHaveAccount")} <a href="${url.loginUrl}">${msg("doLogIn")}</a></p>

        <@validator.templates/>
        <@validator.script field="password"/>
    </#if>
</@layout.registrationLayout>
