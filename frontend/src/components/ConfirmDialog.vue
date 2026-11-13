<script setup lang="ts">
import { nextTick, ref, watch } from 'vue'
import { confirmation as state, finishConfirmation } from '../confirm'
const dialog = ref<HTMLDialogElement>()
watch(() => state.open, async open => { await nextTick(); if (open) dialog.value?.showModal() })
function closed() { const value = dialog.value?.returnValue; if (dialog.value) dialog.value.returnValue = ''; finishConfirmation(value === 'confirm') }
</script>
<template>
  <dialog ref="dialog" aria-labelledby="confirm-title" @close="closed">
    <h2 id="confirm-title">{{ state.title }}</h2><p class="whitespace-pre-line">{{ state.message }}</p>
    <form method="dialog" class="actions"><button value="cancel" autofocus>取消</button><button value="confirm" :class="state.dangerous ? 'danger' : 'primary'">{{ state.button }}</button></form>
  </dialog>
</template>
